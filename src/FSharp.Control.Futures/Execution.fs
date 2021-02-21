namespace rec FSharp.Control.Futures.Execution

open System
open System.Threading
open FSharp.Control.Futures


// TODO: Add IDisposable Runtime interface
// Run future execution
type IExecutor =
    abstract Run: fut: Future<'a> -> 'a
    abstract RunCatch: fut: Future<'a> -> Result<'a, exn>

    abstract RunAsync: fut: Future<'a> -> Future<'a>
    abstract RunCatchAsync: fut: Future<'a> -> Future<Result<'a, exn>>

// -------------------
// ThreadPollScheduler
// -------------------
module private Scheduler =

    [<Struct>]
    type JoinHandleResult<'a> =
        | NoResult
        | Value of value: 'a
        | Exn of exn: exn

        member this.HasResult =
            match this with
            | NoResult -> false
            | _ -> true

        member this.GetValueResult() =
            match this with
            | Value x -> Ok x
            | Exn ex -> Result.Error ex
            | NoResult -> raise (invalidOp "No result for GetValueResult")

        member this.GetValue() =
            match this with
            | Value x -> x
            | _ -> raise (invalidOp "No result for GetValueResult")

    type JoinHandleAwaitFuture<'a>() =
        inherit FSharpFunc<Waker, Poll<'a>>()

        let mutable _value = ValueNone
        let mutable _waker = ValueNone

        member this.Wake(value) =
            _value <- ValueSome value
            match _waker with
            | ValueNone -> ()
            | ValueSome waker -> waker ()

        override this.Invoke(waker) =
            match _value with
            | ValueSome v ->
                _waker <- ValueNone
                Ready v
            | ValueNone ->
                _waker <- ValueSome waker
                Pending

    type JoinHandle<'a>() =
        let mutable result: JoinHandleResult<'a> = NoResult
        let mutable onPutResult: (Result<'a, exn> -> unit) option = None
        let mutable isDisposed = false

        member _.IsDisposed = isDisposed

        member _.IsAwaited = onPutResult.IsSome

        member _.HasResult = result.HasResult

        member x.GetResult() =
            match result with
            | Value res -> Ok res
            | Exn ex -> Error ex
            | _ -> failwith "Unexpected no result"

        member this.SetOnPutResult(waker) =
            if isDisposed then raise (ObjectDisposedException("JoinHandle"))
            if onPutResult.IsSome then raise (invalidOp "JoinHandle already contains waiter")
            onPutResult <- Some waker

        member internal _.PutJoinResult(x) =
            if result.HasResult then invalidOp "Double PutResult"
            if isDisposed then raise (ObjectDisposedException("JoinHandle"))
            result <- x
            match onPutResult with
            | Some onPut ->
                match x with
                | Value x -> onPut (Ok x)
                | Exn ex -> onPut (Error ex)
                | NoResult -> invalidOp "Try put NoResult"
                onPutResult <- None
            | _ -> ()

        member x.PutValue(v) = x.PutJoinResult(JoinHandleResult.Value v)

        member x.PutError(ex) = x.PutJoinResult(JoinHandleResult.Exn ex)

        member this.Wait(timeout: int) : Result<'a, exn> option =
            // Check if a result is available.
            match result with
            | Value r -> Some (Ok r)
            | Exn ex -> Some (Result.Error ex)
            | NoResult ->
                if isDisposed then raise (ObjectDisposedException("JoinHandle"))
                if this.IsAwaited then invalidOp "JoinHandle already waited"
                // Force the creation of the WaitHandle
                use resHandle = new ManualResetEvent(this.HasResult)
                this.SetOnPutResult(fun _ -> resHandle.Set() |> ignore)
                // Check again. While we were in GetWaitHandle, a call to RegisterResult may have set result then skipped the
                // Set because the resHandle wasn't forced.
                match result with
                | Value r -> Some (Ok r)
                | Exn ex -> Some (Result.Error ex)
                | NoResult ->
                    // OK, let's really wait for the Set signal. This may block.
                    let isOk = resHandle.WaitOne(timeout, true)
                    if isOk then
                        // Now the result really must be available
                        match result with
                        | Value r -> Some (Ok r)
                        | Exn ex -> Some (Result.Error ex)
                        | NoResult -> invalidOp "Ooops"
                    else
                        // timed out
                        None

        member this.Wait() : Result<'a, exn> = this.Wait(Timeout.Infinite) |> Option.get

        member this.Await() =
            match result with
            | Value r -> Future.ready (Ok r)
            | Exn ex -> Future.ready (Result.Error ex)
            | NoResult ->
                if this.IsAwaited then invalidOp "JoinHandle already waited"
                let future = JoinHandleAwaitFuture<_>()
                this.SetOnPutResult(future.Wake)
                Future.Core.create future.Invoke

        interface IDisposable with
            // After Dispose JoinHandle can only store putted value
            member _.Dispose() =
                isDisposed <- true

    type IScheduler =
        abstract member Spawn: Future<'a> -> JoinHandle<'a>

    type SchedulerTask<'a>(future: Future<'a>) =
        let handle = new JoinHandle<'a>()
        let sync = obj()
        let mutable requireWake = false

        member _.Future = future
        member _.Sync = sync
        member _.Handle = handle
        member val RequireWake = requireWake with get, set

        member _.Wake() =
            lock sync ^fun () ->
                requireWake <- true
        interface IDisposable with member _.Dispose() = (handle :> IDisposable).Dispose()

    type ThreadPollScheduler() =

        member internal this.CreateTaskWaker(task: SchedulerTask<'a>) =
            fun () -> this.WakeTask(task)

        member internal this.WakeTask(task: SchedulerTask<'a>) =
            lock task.Sync ^fun () ->
                if task.RequireWake then
                    let waker = this.CreateTaskWaker(task)
                    let poolAction (obj) =
                        try
                            let result = Future.Core.poll waker task.Future
                            match result with
                            | Ready x ->
                                task.Handle.PutValue(x)
                                (task.Handle :> IDisposable).Dispose()
                            | Pending -> ()
                        with
                        | ex ->
                            task.Handle.PutError(ex)
                            (task.Handle :> IDisposable).Dispose()
                    ThreadPool.QueueUserWorkItem(fun _ -> poolAction()) |> ignore
                else ()

        interface IScheduler with
            member this.Spawn(fut) =
                let task = new SchedulerTask<'a>(fut)
                task.RequireWake <- true
                this.WakeTask(task)
                task.Handle

module private Result =
    let getOrRaise result =
        match result with
        | Ok x -> x
        | Error ex -> raise ex

module Future =
    let onRuntime rt fut = future {
        do Executor.enter rt
        return! fut
    }

type private SchedulingFutureRt(scheduler: Scheduler.IScheduler) =

    static let instance = SchedulingFutureRt(Scheduler.ThreadPollScheduler())
    static member GetThreadPoolInstance() = instance

    interface IExecutor with
        member this.Run(fut) =
            scheduler.Spawn(Future.onRuntime this fut).Wait() |> Result.getOrRaise

        member this.RunCatch(fut) =
            scheduler.Spawn(Future.onRuntime this fut).Wait()

        member this.RunAsync(fut) =
            scheduler.Spawn(Future.onRuntime this fut).Await() |> Future.map Result.getOrRaise

        member this.RunCatchAsync(fut) =
            scheduler.Spawn(Future.onRuntime this fut).Await()


type LocalFutureRt() =

    static let instance = LocalFutureRt()
    static member GetInstance() = instance

    interface IExecutor with
        member _.Run(fut) = fut |> Future.runSync
        member _.RunAsync(fut) = fut |> Future.runSync |> Future.ready

        member x.RunCatch(fut) = fut |> Future.catch |> Future.runSync
        member x.RunCatchAsync(fut) = fut |> Future.catch |> Future.runSync |> Future.ready


exception CurrentFutureRtIsNotSet of string

[<RequireQualifiedAccess>]
module Executor =

    /// Future runtime on current thread.
    let localRt = LocalFutureRt.GetInstance () :> IExecutor

    /// Future runtime on thread pool
    let threadPoolRt = SchedulingFutureRt.GetThreadPoolInstance () :> IExecutor

    // todo: dispose it
    // todo: Add optional lock of current runtime for using inside runtime thread for block rt switch by user
    let private currentRt = new ThreadLocal<IExecutor voption>()

    let private getCurrentRt () =
        if currentRt.IsValueCreated then
            currentRt.Value
        else
            ValueNone

    let private getCurrentRtOrRaise () =
        match getCurrentRt () with
        | ValueSome rt -> rt
        | ValueNone -> raise (CurrentFutureRtIsNotSet "")

    /// Current thread "enter" into IFutureRt context
    let enter (rt: IExecutor) : unit =
        currentRt.Value <- ValueSome rt

    /// Current thread "exit" from IFutureRt context
    let exit =
        currentRt.Value <- ValueNone

    let runOn (rt: IExecutor) fut = rt.Run(fut)
    let runCatchOn (rt: IExecutor) fut = rt.RunCatch(fut)
    let runAsyncOn (rt: IExecutor) fut = rt.RunAsync(fut)
    let runCatchAsyncOn (rt: IExecutor) fut = rt.RunCatchAsync(fut)

    let run fut = runOn (getCurrentRtOrRaise ()) fut
    let runCatch fut = runCatchOn (getCurrentRtOrRaise ()) fut
    let runAsync fut = runAsyncOn (getCurrentRtOrRaise ()) fut
    let runCatchAsync fut = runCatchAsyncOn (getCurrentRtOrRaise ()) fut
