module ArchiverTemplate.Handler

type Stats(log, statsInterval, stateInterval) =
    inherit Propulsion.Streams.Sync.Stats<unit>(log, statsInterval, stateInterval)

    override __.HandleOk(()) = ()
    override __.HandleExn exn = log.Information(exn, "Unhandled")

let (|Archivable|NotArchivable|) = function
    | "LokiPickTicketReservations"
    | "LokiDcBatch"
    | "LokiDcTransmissions" ->
        Archivable
    | _ ->
        NotArchivable

let transformOrFilter (changeFeedDocument: Microsoft.Azure.Documents.Document) : Propulsion.Streams.StreamEvent<_> seq = seq {
    for batch in Propulsion.Cosmos.EquinoxCosmosParser.enumStreamEvents changeFeedDocument do
        let (FsCodec.StreamName.CategoryAndId (cat,_)) = batch.stream
        match cat with
        | Archivable -> yield batch
        | NotArchivable -> ()
}
