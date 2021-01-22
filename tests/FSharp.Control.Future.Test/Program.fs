﻿open System
open System.Diagnostics
open System.Threading.Tasks
open System.Threading

open FSharp.Control.Tasks.V2

open FSharp.Control.Future


module Snowball =
    
    type Tree<'a> =
        | Leaf
        | Node of Tree<'a> * 'a * Tree<'a>
    
    let snowballFuture n = future {
        let rec loop x i : Future<Tree<int>> = future {
            if i >= n then
                return Leaf
            else
            let! left = loop (x + i) (i + 1)
            let! right = loop (x + i + 1) (i + 1)
            return Node (left, x ,right)
        }
        return! loop 0 0
    }
    
    let snowballFutureParallel n = future {
        let rec loop x i : Future<Tree<int>> = future {
            if i >= n then
                return Leaf
            else            
            let left = loop (x + i) (i + 1) |> Runtime.runOnPoolAsync
            let right = loop (x + i + 1) (i + 1) |> Runtime.runOnPoolAsync
            let! left = left
            let! right = right
            return Node (left, x ,right)
        }
        return! loop 0 0
    }
    
    let snowballAsyncParallel n = async {
        let rec loop x i : Async<Tree<int>> = async {
            if i >= n then
                return Leaf
            else 
            let left = loop (x + i) (i + 1)
            let right = loop (x + i + 1) (i + 1)
            let! [|left; right|] = Async.Parallel [left; right]
            return Node (left, x ,right)
        }
        return! loop 0 0
    }
    
    let snowballAsync n = async {
        let rec loop x i : Async<Tree<int>> = async {
            if i >= n then
                return Leaf
            else 
            let! left = loop (x + i) (i + 1)
            let! right = loop (x + i + 1) (i + 1)
            return Node (left, x ,right)
        }
        return! loop 0 0
    }
    
    let snowballTask n = task {
        let rec loop x i : Task<Tree<int>> = task {
            if i >= n then
                return Leaf
            else 
            let! left = loop (x + i) (i + 1)
            let! right = loop (x + i + 1) (i + 1)
            return Node (left, x ,right)
        }
        return! loop 0 0
    }


let runFuture depth =
    let f = Snowball.snowballFuture depth
    Runtime.runSync f

let runFutureParallel depth =
    let f = Snowball.snowballFutureParallel depth
    Runtime.runSync f

let runAsync depth =
    let a = Snowball.snowballAsync depth
    Async.RunSynchronously a

let runAsyncParallel depth =
    let a = Snowball.snowballAsyncParallel depth
    Async.RunSynchronously a

let runTask depth =
    let task = Snowball.snowballTask depth
    task.GetAwaiter().GetResult()


[<EntryPoint>]
let main argv =
    
    printfn "Main"
    future {
        let! x1 = future {
            printfn "[x1] Start"
            do! Future.sleep 2000
            printfn "[x1] End"
            return 2
        }
        and! x2 = future {
            printfn "[x2] Start"
            do! Future.sleep 3000
            printfn "[x2] End"
            return 3
        }
        and! x3 = future {
            printfn "[x3]"
            return 1
        }
        printfn "Merged"
        return x1 + x2 + x3
    }
    |> Runtime.runSync
    |> printfn "R: %i"
    
    0 // return an integer exit code
    