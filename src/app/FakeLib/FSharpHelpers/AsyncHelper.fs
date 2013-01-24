[<AutoOpen>]
module Fake.AsyncHelper

/// Runs the given function on all items in parallel
let inline doParallel f items =
    items
    |> Seq.map (fun element -> async { return f element})
    |> Async.Parallel
    |> Async.RunSynchronously

type ThrottleMessage<'a> = 
    | AddJob of (Async<'a>*AsyncReplyChannel<'a>) 
    | DoneJob of ('a*AsyncReplyChannel<'a>) 
    | Stop

/// This agent accumulates 'jobs' but limits the number which run concurrently.
type ThrottledAsync<'a>(limit) =    
    let agent = MailboxProcessor<ThrottleMessage<'a>>.Start(fun inbox ->
        let rec loop(jobs, count) = async {
            let! msg = inbox.Receive()  //get next message
            match msg with
            | AddJob(job) -> 
                if count < limit then   //if not at limit, we work, else loop
                    return! work(job::jobs, count)
                else
                    return! loop(job::jobs, count)
            | DoneJob(result, reply) -> 
                reply.Reply(result)           //send back result to caller
                return! work(jobs, count - 1) //no need to check limit here
            | Stop -> return () }
        and work(jobs, count) = async {
            match jobs with
            | [] -> return! loop(jobs, count) //if no jobs left, wait for more
            | (job, reply)::jobs ->          //run job, post Done when finished
                async { let! result = job 
                        inbox.Post(DoneJob(result, reply)) }
                |> Async.Start
                return! loop(jobs, count + 1) //job started, go back to waiting
        }
        loop([], 0)
    )
    member m.AddJob(job) = agent.PostAndAsyncReply(fun rep-> AddJob(job, rep))
    member m.Stop() = agent.Post(Stop)
    static member RunJobs limit jobs = 
        let agent = ThrottledAsync<'a>(limit)
        let res = jobs |> Seq.map (fun job -> agent.AddJob(job))
                       |> Async.Parallel
                       |> Async.RunSynchronously
        agent.Stop()
        res
