[<AutoOpen>]
module FSharp.Control.Futures.Transforms

open FSharp.Control.Futures
open FSharp.Control.Futures.Base


[<AutoOpen>]
module FutureAsyncTransforms =

    [<RequireQualifiedAccess>]
    module Future =

        open System
        open FSharp.Control.Futures.Cancellable

        [<RequireQualifiedAccess>]
        type AsyncResult<'a> =
            | Pending
            | Completed of 'a
            | Errored of exn
            | Cancelled of OperationCanceledException

        let ofAsync (x: Async<'a>) : CancellableFuture<'a> =
            let mutable result = AsyncResult.Pending
            let mutable started = false
            Future.create ^fun waker ->
                if not started then
                    started <- true
                    Async.StartWithContinuations(
                        x,
                        (fun r -> result <- AsyncResult.Completed r; waker ()),
                        (fun e -> result <- AsyncResult.Errored e; waker ()),
                        (fun ec -> result <- AsyncResult.Cancelled ec; waker ())
                    )
                ()
                match result with
                | AsyncResult.Pending -> Pending
                | AsyncResult.Completed result -> Ready ^ MaybeCancel.Completed result
                | AsyncResult.Cancelled ec -> Ready ^ MaybeCancel.Cancelled ec
                | AsyncResult.Errored e -> raise e

        // TODO: Implement without blocking
        let toAsync (x: IFuture<'a>) : Async<'a> =
            async {
                let r = x |> Future.run
                return r
            }


[<AutoOpen>]
module FutureTaskTransforms =

    [<RequireQualifiedAccess>]
    module Future =

        open System.Threading.Tasks

        let ofTask (x: Task<'a>) : IFuture<'a> =
            let mutable result = ValueNone
            let mutable started = false
            Future.create ^fun waker ->
                if not started then
                    started <- true
                    // TODO: Ensure to correct task binding
                    x.ContinueWith(fun (x: Task<'a>) ->
                        result <- ValueSome x.Result
                        waker ()
                        Task.CompletedTask
                    ) |> ignore
                match result with
                | ValueNone -> Pending
                | ValueSome result -> Ready result

        // TODO: Implement without blocking
        let toTask (x: IFuture<'a>) : Task<'a> =
            Task<'a>.Factory.StartNew(
                fun () ->
                    x |> Future.run
            )

        // TODO: Implement without blocking
        let toTaskOn (scheduler: TaskScheduler) (x: IFuture<'a>) : Task<'a> =
            TaskFactory<'a>(scheduler).StartNew(
                fun () ->
                    x |> Future.run
            )