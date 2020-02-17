namespace Fc.Inventory

open Epoch
open Equinox.Core // we use Equinox's AsyncCacheCell helper below
open FSharp.UMX

type internal IdsCache<'Id>() =
    let all = System.Collections.Concurrent.ConcurrentDictionary<'Id,unit>() // Bounded only by relatively low number of physical pick tickets IRL
    static member Create init = let x = IdsCache() in x.Add init; x
    member __.Add ids = for x in ids do all.[x] <- ()
    member __.Contains id = all.ContainsKey id

/// Maintains active Epoch Id in a thread-safe manner while ingesting items into the `series` of `epochs`
/// Prior to first add, reads `lookBack` epochs to seed the cache, in order to minimize the number of duplicated Ids we ingest
type Service internal (inventoryId, series : Series.Service, epochs : Epoch.Service, lookBack, capacity) =

    let log = Serilog.Log.ForContext<Service>()

    // Maintains what we believe to be the currently open EpochId
    // Guaranteed to be set only after `previousIds.AwaitValue()`
    let mutable activeEpochId = Unchecked.defaultof<_>

    // We want max one request in flight to establish the pre-existing Batches from which the tickets cache will be seeded
    let previousEpochs = AsyncCacheCell<AsyncCacheCell<Set<InventoryTransactionId>> list> <| async {
        let! startingId = series.ReadIngestionEpoch(inventoryId)
        activeEpochId <- %startingId
        let read epochId = async { let! r = epochs.TryIngest(inventoryId, epochId, (fun _ -> 1),Seq.empty) in return r.transactionIds }
        return [ for epoch in (max 0 (%startingId - lookBack)) .. (%startingId - 1) -> AsyncCacheCell(read %epoch) ] }

    // TransactionIds cache - used to maintain a list of transactions that have already been ingested in order to avoid db round-trips
    let previousIds : AsyncCacheCell<IdsCache<_>> = AsyncCacheCell <| async {
        let! batches = previousEpochs.AwaitValue()
        let! ids = seq { for x in batches -> x.AwaitValue() } |> Async.Parallel
        return IdsCache.Create(Seq.concat ids) }

    let tryIngest events = async {
        let! previousIds = previousIds.AwaitValue()
        let initialEpochId = %activeEpochId

        let rec aux epochId totalIngested items = async {
            let SeqPartition f = Seq.toArray >> Array.partition f
            let dup,fresh = items |> SeqPartition (Epoch.Events.chooseInventoryTransactionId >> Option.exists previousIds.Contains)
            let fullCount = List.length items
            let dropping = fullCount - Array.length fresh
            if dropping <> 0 then log.Information("Ignoring {count}/{fullCount} duplicate ids: {ids} for {epochId}", dropping, fullCount, dup, epochId)
            if Array.isEmpty fresh then
                return totalIngested
            else
                let! res = epochs.TryIngest(inventoryId, epochId, capacity, fresh)
                log.Information("Added {count} items to {inventoryId:l}/{epochId}", res.added, inventoryId, epochId)
                // The adding is potentially redundant; we don't care
                previousIds.Add res.transactionIds
                // Any writer noticing we've moved to a new epoch shares the burden of marking it active
                if not res.isClosed && activeEpochId < %epochId then
                    log.Information("Marking {inventoryId:l}/{epochId} active", inventoryId, epochId)
                    do! series.AdvanceIngestionEpoch(inventoryId, epochId)
                    System.Threading.Interlocked.CompareExchange(&activeEpochId, %epochId,activeEpochId) |> ignore
                let totalIngestedTickets = totalIngested + res.added
                match res.rejected with
                | [] -> return totalIngestedTickets
                | rej -> return! aux (InventoryEpochId.next epochId) totalIngestedTickets rej }
        return! aux initialEpochId 0 events
    }

    /// Upon startup, we initialize the PickTickets cache with recent batches; we want to kick that process off before our first ingest
    member __.Initialize() = previousIds.AwaitValue() |> Async.Ignore

    /// Feeds the events into the sequence of batches. Returns the number of items actually added [excluding duplicates]
    member __.Ingest(events : Epoch.Events.Event list) : Async<int> = tryIngest events

module internal Helpers =

    let create inventoryId maxTransactionsPerBatch lookBackLimit (series, epochs) =
        let remainingBatchCapacity (state: Epoch.Fold.State) =
            let currentLen = state.ids.Count
            max 0 (maxTransactionsPerBatch - currentLen)
        Service(inventoryId, series, epochs, lookBack = lookBackLimit, capacity = remainingBatchCapacity)

module Cosmos =

    let create inventoryId maxTransactionsPerBatch lookBackLimit (context, cache) =
        let series = Series.Cosmos.createService (context, cache)
        let epochs = Epoch.Cosmos.createService (context, cache)
        Helpers.create inventoryId maxTransactionsPerBatch lookBackLimit (series, epochs)
