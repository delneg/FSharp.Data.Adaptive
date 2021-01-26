﻿module ASet

open NUnit.Framework
open FsCheck
open FSharp.Data.Adaptive
open FSharp.Data.Traceable
open FsUnit
open FsCheck.NUnit

type Record<'T> = { value : 'T }

//[<AutoOpen>]
//module Helpers =
//    let check (r : IHashSetReader<'T>) =
//        let a = r.Adaptive.GetChanges FSharp.Data.Adaptive.AdaptiveToken.Top
//        let r = r.Reference.GetChanges FSharp.Data.Adaptive.Reference.AdaptiveToken.Top
//        a |> should setequal r
//        r

let emptyDelta = HashSetDelta.empty<int>

open FSharp.Data
open Generators
[<Property(MaxTest = 500, Arbitrary = [| typeof<Generators.AdaptiveGenerators> |]); Timeout(60000)>]
let ``[ASet] reference impl``() ({ sreal = real; sref = ref; sexpression = str; schanges = changes } : VSet<int>) =
    printfn "VALIDATE"


    let str b =
        let m, str = str b
        String.concat "\r\n" [
            for (k,v) in Map.toSeq m do
                yield sprintf "let %s = %s" k v
            yield str
        ]

    printfn "%s" (Generators.Generators.indent (Generators.Generators.indent (str false)))
    let r = real.GetReader()

    let check (beforeChangeStr : string) (beforeChange : list<string>) (latestChanges : list<string>) = 
        r.GetChanges AdaptiveToken.Top |> ignore
        let vReal = real.Content.GetValue AdaptiveToken.Top // |> CountingHashSet.toHashSet
        let vRef = ref.Content.GetValue Reference.AdaptiveToken.Top

        let delta = HashSet.computeDelta vReal vRef |> HashSetDelta.toList
        match delta with
        | [] ->
            vRef
        | delta ->
            let real = vReal |> Seq.sort |> Seq.map string |> String.concat "; " |> sprintf "[%s]"
            let ref = vRef |> Seq.sort |> Seq.map string |> String.concat "; " |> sprintf "[%s]"
            let delta = delta |> Seq.sortBy (fun v -> v.Value) |> Seq.map string |> String.concat "; "  |> sprintf "[%s]"
            
            let inputs = changes() |> List.map (fun i -> i.cell)

            let message =
                String.concat "\r\n" [
                    yield "ERROR"
                    yield "BEFORE"
                    yield! beforeChangeStr.Split([|"\r\n"|], System.StringSplitOptions.None) |> Array.map Generators.indent
                    
                    yield "CURRENT"
                    yield! (str true).Split([|"\r\n"|], System.StringSplitOptions.None) |> Array.map Generators.indent

                    yield sprintf "real:  %s" real
                    yield sprintf "ref:   %s" ref
                    yield sprintf "delta: %s" delta
                    
                    yield "before"
                    for i in beforeChange do
                        yield sprintf "   %A" i

                    yield "inputs"
                    for i in inputs do
                        yield sprintf "   %A" i

                    yield "latest changes"
                    for c in latestChanges do
                        yield "   " + c
                ]
            failwith message
        //printfn "    VALUE => %A" vRef
             
    let mutable lastValue = check "" [] []

    let run = 
        gen {
            let mutable effective = 0

            while effective < 20 do
                let all = changes() 
                match all with
                | [] -> 
                    effective <- System.Int32.MaxValue
                | _ -> 
                    let! some = 
                        all
                        |> List.map (fun g -> g.change) 
                        |> Gen.subListOf
                        |> Gen.filter (List.isEmpty >> not)

                    let beforeChange =
                        all |> List.map (fun c -> c.cell |> string)

                    let beforeChangeStr = str true
                    let! changeAll = Gen.collect id some
                    let latestChange = 
                        transact (fun () ->
                            changeAll |> List.map (fun c -> c())
                        )
                    let v = check beforeChangeStr beforeChange latestChange
                    if not (DefaultEquality.equals v lastValue) then
                        
                        printfn "  change %d => %A" effective v
                        lastValue <- v
                    

     
                    effective <- effective + 1
        }

    Gen.eval 15 (Random.newSeed()) run

[<Test>]
let ``[ASet] mapUse``() =
    let input = cset [1;2;3;4]
    let refCount = ref 0

    let newDisposable() =
        System.Threading.Interlocked.Increment(&refCount.contents) |> ignore
        { new System.IDisposable with
            member x.Dispose() =
                System.Threading.Interlocked.Decrement(&refCount.contents) |> ignore
                
        }

    let disp, set = input |> ASet.mapUse (fun v -> newDisposable())

    !refCount |> should equal 0

    let r = set.GetReader()
    r.GetChanges(AdaptiveToken.Top) |> ignore
    (fst r.State).Count |> should equal 4
    !refCount |> should equal 4

    transact (fun () -> input.Remove 1 |> ignore)
    r.GetChanges(AdaptiveToken.Top) |> ignore
    (fst r.State).Count |> should equal 3
    !refCount |> should equal 3
    
    transact (fun () -> input.Add 7 |> ignore)
    r.GetChanges(AdaptiveToken.Top) |> ignore
    (fst r.State).Count |> should equal 4
    !refCount |> should equal 4

    disp.Dispose()
    r.GetChanges(AdaptiveToken.Top) |> ignore
    (fst r.State).Count |> should equal 0
    !refCount |> should equal 0
    
    // double free resistance
    disp.Dispose()
    r.GetChanges(AdaptiveToken.Top) |> ignore
    (fst r.State).Count |> should equal 0
    !refCount |> should equal 0

[<Test>]
let ``[CSet] contains/isEmpty/count`` () =
    let set = cset(HashSet.ofList [1;2])

    set.IsEmpty |> should be False
    set.Count |> should equal 2
    set.Contains 1 |> should be True
    set.Contains 2 |> should be True

    transact (fun () ->
        set.Remove 2 |> should be True
    )
    
    set.IsEmpty |> should be False
    set.Count |> should equal 1
    set.Contains 1 |> should be True
    set.Contains 2 |> should be False

    
    transact (fun () ->
        set.Remove 1 |> should be True
    )
    
    set.IsEmpty |> should be True
    set.Count |> should equal 0
    set.Contains 1 |> should be False
    set.Contains 2 |> should be False

[<Test>]
let ``[CSet] intersectWith`` () =
    let s = cset [1;2;3;4]

    transact (fun () ->
        s.IntersectWith [2;3;5]
    )
    
    s.Value |> should equal (HashSet.OfList [2;3])


[<Test>]
let ``[ASet] reduce group``() =
    let set = cset [1;2;3]

    let reduction = AdaptiveReduction.sum()
    let res = ASet.reduce reduction set

    res |> AVal.force |> should equal 6

    transact (fun () -> set.Add 4 |> ignore)
    res |> AVal.force |> should equal 10

    transact (fun () -> set.Remove 1 |> ignore)
    res |> AVal.force |> should equal 9

    transact (fun () -> set.Clear())
    res |> AVal.force |> should equal 0
    
[<Test>]
let ``[ASet] reduce half group``() =
    let list = cset [1;2;3]

    let reduction = AdaptiveReduction.product()
    let res = ASet.reduce reduction list

    res |> AVal.force |> should equal 6

    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 24

    transact (fun () -> list.Remove 1 |> ignore)
    res |> AVal.force |> should equal 24

    transact (fun () -> list.Clear())
    res |> AVal.force |> should equal 1
    
    transact (fun () -> list.Add 0 |> ignore)
    res |> AVal.force |> should equal 0
    
    transact (fun () -> list.Add 10 |> ignore)
    res |> AVal.force |> should equal 0
    
    transact (fun () -> list.Add 2 |> ignore)
    res |> AVal.force |> should equal 0
    
    transact (fun () -> list.Remove 0 |> ignore)
    res |> AVal.force |> should equal 20

    
[<Test>]
let ``[ASet] reduce empty after lots of operations``() =
    let s = cset<float>()
    let r = ASet.sum s
    let rand = System.Random()
    transact (fun () ->
        for i in 1 .. 10000 do
            s.Add(rand.NextDouble()) |> ignore
    )
    r |> AVal.force |> ignore
    
    transact s.Clear
    r |> AVal.force |> should equal 0.0

    transact (fun () ->
        for i in 1 .. 10000 do
            s.Add(rand.NextDouble()) |> ignore
    )

    let element = s.Value |> Seq.item (rand.Next(s.Count))
    transact (fun () -> s.Value <- HashSet.single element)
    
    r |> AVal.force |> should equal element



[<Test>]
let ``[ASet] reduce fold``() =
    let list = cset [1;2;3]

    let reduction = AdaptiveReduction.fold 0 (+)
    let res = ASet.reduce reduction list

    res |> AVal.force |> should equal 6

    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 10

    transact (fun () -> list.Remove 1 |> ignore)
    res |> AVal.force |> should equal 9

    transact (fun () -> list.Clear())
    res |> AVal.force |> should equal 0


[<Test>]
let ``[ASet] reduceBy group``() =
    let list = cset [1;2;3]

    let reduction = AdaptiveReduction.sum()
    let res = ASet.reduceBy reduction float list

    res |> AVal.force |> should equal 6.0

    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 10.0

    transact (fun () -> list.Remove 1 |> ignore)
    res |> AVal.force |> should equal 9.0

    transact (fun () -> list.Clear())
    res |> AVal.force |> should equal 0.0
    
[<Test>]
let ``[ASet] reduceBy half group``() =
    let list = cset [1;2;3]

    let reduction = AdaptiveReduction.product()
    let res = ASet.reduceBy reduction float list

    res |> AVal.force |> should equal 6.0

    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 24.0

    transact (fun () -> list.Remove 1 |> ignore)
    res |> AVal.force |> should equal 24.0

    transact (fun () -> list.Clear())
    res |> AVal.force |> should equal 1.0
    
    transact (fun () -> list.Add 0 |> ignore)
    res |> AVal.force |> should equal 0.0
    
    transact (fun () -> list.Add 10 |> ignore)
    res |> AVal.force |> should equal 0.0
    
    transact (fun () -> list.Add 2 |> ignore)
    res |> AVal.force |> should equal 0.0
    
    transact (fun () -> list.Remove 0 |> ignore)
    res |> AVal.force |> should equal 20.0

[<Test>]
let ``[ASet] reduceBy fold``() =
    let list = cset [1;2;3]

    let reduction = AdaptiveReduction.fold 0.0 (+)
    let res = ASet.reduceBy reduction float list

    res |> AVal.force |> should equal 6.0

    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 10.0

    transact (fun () -> list.Remove 1 |> ignore)
    res |> AVal.force |> should equal 9.0

    transact (fun () -> list.Clear())
    res |> AVal.force |> should equal 0.0

    
[<Test>]
let ``[ASet] reduceByA group``() =
    let list = cset [1;2;3]
    
    let even = cval 1
    let odd = cval 0

    let mapping v =
        if v % 2 = 0 then even :> aval<_>
        else odd :> aval<_>

    let reduction = AdaptiveReduction.sum()
    let res = ASet.reduceByA reduction mapping list
    
    // (1,0) (2,1) (3,0) = 1
    res |> AVal.force |> should equal 1

    // (1,0) (2,2) (3,0) = 2
    transact (fun () -> even.Value <- 2)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) = 1
    transact (fun () -> even.Value <- 1)
    res |> AVal.force |> should equal 1

    // (1,3) (2,1) (3,3) = 7
    transact (fun () -> odd.Value <- 3)
    res |> AVal.force |> should equal 7

    // (1,1) (2,0) (3,1) = 2
    transact (fun () -> odd.Value <- 1; even.Value <- 0)
    res |> AVal.force |> should equal 2
    
    // (1,1) (2,0) (3,1) (4,0) = 2
    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) = 2
    transact (fun () -> odd.Value <- 0; even.Value <- 1)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) (5,0) = 2
    transact (fun () -> list.Add 5 |> ignore)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) (5,0) (6,1) = 3
    transact (fun () -> list.Add 6 |> ignore)
    res |> AVal.force |> should equal 3

    
    // (2,1) (4,1) (6,1) = 3
    transact (fun () -> 
        list.Remove 5 |> ignore
        list.Remove 3 |> ignore
        list.Remove 1 |> ignore
        odd.Value <- 1
    )
    res |> AVal.force |> should equal 3
    
    // (1,1) (3,1) (5,1) = 3
    transact (fun () -> list.Value <- HashSet.ofList [1;3;5])
    res |> AVal.force |> should equal 3

     
[<Test>]
let ``[ASet] reduceByA half group``() =
    let list = cset [1;2;3]
    
    let even = cval 1
    let odd = cval 0

    let mapping v =
        if v % 2 = 0 then even :> aval<_>
        else odd :> aval<_>

    let mutable fails = 0
    let reduction = 
        { AdaptiveReduction.sum() with
            sub = fun s v -> 
                if s % 2 = 0 then ValueSome (s - v)
                else fails <- fails + 1; ValueNone
        }

    let res = ASet.reduceByA reduction mapping list
    
    // (1,0) (2,1) (3,0) = 1
    res |> AVal.force |> should equal 1

    // (1,0) (2,2) (3,0) = 2
    transact (fun () -> even.Value <- 2)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) = 1
    transact (fun () -> even.Value <- 1)
    res |> AVal.force |> should equal 1

    // (1,3) (2,1) (3,3) = 7
    transact (fun () -> odd.Value <- 3)
    res |> AVal.force |> should equal 7

    // (1,1) (2,0) (3,1) = 2
    transact (fun () -> odd.Value <- 1; even.Value <- 0)
    res |> AVal.force |> should equal 2
    
    // (1,1) (2,0) (3,1) (4,0) = 2
    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) = 2
    transact (fun () -> odd.Value <- 0; even.Value <- 1)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) (5,0) = 2
    transact (fun () -> list.Add 5 |> ignore)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) (5,0) (6,1) = 3
    transact (fun () -> list.Add 6 |> ignore)
    res |> AVal.force |> should equal 3

    
    // (2,1) (4,1) (6,1) = 3
    transact (fun () -> 
        list.Remove 1 |> ignore
        list.Remove 3 |> ignore
        list.Remove 5 |> ignore
        odd.Value <- 1
    )
    res |> AVal.force |> should equal 3
    
    // (1,1) (3,1) (5,1) = 3
    transact (fun () -> list.Value <- HashSet.ofList [1;3;5])
    res |> AVal.force |> should equal 3
   
    fails |> should be (greaterThan 0)
     
[<Test>]
let ``[ASet] reduceByA fold``() =
    let list = cset [1;2;3]
    
    let even = cval 1
    let odd = cval 0

    let mapping v =
        if v % 2 = 0 then even :> aval<_>
        else odd :> aval<_>

    let reduction = AdaptiveReduction.fold 0 (+)

    let res = ASet.reduceByA reduction mapping list
    
    // (1,0) (2,1) (3,0) = 1
    res |> AVal.force |> should equal 1

    // (1,0) (2,2) (3,0) = 2
    transact (fun () -> even.Value <- 2)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) = 1
    transact (fun () -> even.Value <- 1)
    res |> AVal.force |> should equal 1

    // (1,3) (2,1) (3,3) = 7
    transact (fun () -> odd.Value <- 3)
    res |> AVal.force |> should equal 7

    // (1,1) (2,0) (3,1) = 2
    transact (fun () -> odd.Value <- 1; even.Value <- 0)
    res |> AVal.force |> should equal 2
    
    // (1,1) (2,0) (3,1) (4,0) = 2
    transact (fun () -> list.Add 4 |> ignore)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) = 2
    transact (fun () -> odd.Value <- 0; even.Value <- 1)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) (5,0) = 2
    transact (fun () -> list.Add 5 |> ignore)
    res |> AVal.force |> should equal 2
    
    // (1,0) (2,1) (3,0) (4,1) (5,0) (6,1) = 3
    transact (fun () -> list.Add 6 |> ignore)
    res |> AVal.force |> should equal 3

    
    // (2,1) (4,1) (6,1) = 3
    transact (fun () -> 
        list.Remove 1 |> ignore
        list.Remove 3 |> ignore
        list.Remove 5 |> ignore
        odd.Value <- 1
    )
    res |> AVal.force |> should equal 3
    
    // (1,1) (3,1) (5,1) = 3
    transact (fun () -> list.Value <- HashSet.ofList [1;3;5])
    res |> AVal.force |> should equal 3



let checkReader (actual: aset<_>) expected = 
    let reader = actual.GetReader()
    fun () ->
        reader.GetChanges AdaptiveToken.Top |> ignore
        let actualValue = reader.State |> fst |> CountingHashSet.toList

        let expectedValue = expected()
        if actualValue <> expectedValue then 
            printfn "actual = %A, exp = %A" actualValue expectedValue
            failwith "fail"
        actualValue |> should equal expectedValue

[<Test>]
let ``[ASet] range smoke``() =
    let lower = cval 1
    let upper = cval 1
    
    let actual =  ASet.range lower upper
    let expected () = [ lower.Value .. upper.Value ]
    let check = checkReader actual expected

    check ()
    transact (fun () -> 
        lower.Value <- 0
        upper.Value <- 4
    )
    check()

let inline ASetRangeSystematic(low, high) =
    let range = [ low .. high ]
    for pl in range do
        for pu in range do 
           for l in range do 
               for u in range do 
                printfn "checking change from (%A .. %A) to (%A .. %A)" pl pu l u
                let lower = cval pl
                let upper = cval pu
    
                let actual =  ASet.range lower upper
                let expected () = [ lower.Value .. upper.Value ]
                let check = checkReader actual expected

                check ()
                transact (fun () -> 
                    lower.Value <- l
                    upper.Value <- u
                )
                check()
    printfn "checked %d cases" (range.Length * range.Length * range.Length * range.Length)

[<Test>]
let ``[ASet] range systematic int32``() =
    ASetRangeSystematic(0, 4)

[<Test>]
let ``[ASet] range systematic int64``() =
    ASetRangeSystematic(0L, 4L)




