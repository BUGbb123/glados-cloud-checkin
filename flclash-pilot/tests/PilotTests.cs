using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Reflection;
using System.Windows.Forms;
using FlClashPilot;

public static class PilotTests
{
    static int checks;
    static void Check(bool condition,string name) { if(!condition)throw new Exception("FAILED: "+name); checks++; Console.WriteLine("PASS "+name); }
    static Samples Sample(params int?[] values) { var s=new Samples(); foreach(var v in values)s.Add(v); return s; }
    static bool Rejects(string value) { try { Controller.ValidateAddress(value); return false; } catch(PilotException) { return true; } }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            Check(Controller.ValidateAddress("http://localhost:9090")=="http://127.0.0.1:9090","localhost pinned to loopback");
            foreach(var value in new[]{"http://example.com:9090","http://127.0.0.1.example.com:9090","http://127.0.0.1:9090/path","http://u:p@127.0.0.1:9090","http://127.0.0.1:9090/?token=secret","https://127.0.0.1:9090"})Check(Rejects(value),"reject unsafe controller "+value);
            var stats=Sample(100,200,100,null,100,100); Check(stats.Values.Count==5&&stats.Successes==4,"bounded rolling window");
            Check(stats.ConsecutiveFailures==0&&stats.Eligible,"failure recovery eligibility");
            Check(Sample(10,null,10,null).FailureRate==0.5&&!Sample(10,null,10,null).Eligible,"high failure rate excluded");
            Check(Sample(10).Eligible==false,"two successful samples required");
            Check(Sample(100,200).Median==150,"even median");
            var now=DateTime.UtcNow;
            var data=new Dictionary<string,Samples>{{"a",Sample(300,300)},{"b",Sample(100,100)}};
            var fresh=new HashSet<string>{"a","b"}; var policy=new Policy();
            Check(!policy.Choose("a",data,fresh,now,DateTime.MinValue).Switch,"candidate must lead two rounds");
            Check(policy.Choose("a",data,fresh,now,DateTime.MinValue).Switch,"confirmed improvement switches");
            policy=new Policy();policy.Choose("a",data,fresh,now,now.AddSeconds(-10));
            Check(!policy.Choose("a",data,fresh,now,now.AddSeconds(-10)).Switch,"cooldown prevents churn");
            data["a"]=Sample(300,300,null,null);
            Check(new Policy().Choose("a",data,fresh,now,now).Switch,"confirmed failure bypasses cooldown");
            data["a"]=Sample(300,300,null);
            Check(!new Policy().Choose("a",data,fresh,now,DateTime.MinValue).Switch,"single failure retains route");
            data["b"]=Sample(null,null,null);
            Check(!new Policy().Choose("a",data,fresh,now,DateTime.MinValue).Switch,"no viable node means no switch");
            data["a"]=Sample(110,110);data["b"]=Sample(100,100);policy=new Policy();policy.Choose("a",data,fresh,now,DateTime.MinValue);
            Check(!policy.Choose("a",data,fresh,now,DateTime.MinValue).Switch,"small improvement ignored");
            data["a"]=Sample(300,300);data["b"]=Sample(100,100);policy=new Policy();
            Check(!policy.Choose("a",data,new HashSet<string>{"a"},now,DateTime.MinValue).Switch,"stale candidate cannot switch");
            var candidates=Enumerable.Range(0,65).Select(i=>"n"+i).ToList();var covered=new HashSet<string>();
            var records=new Dictionary<string,Samples>{{"n1",Sample(10,10)},{"n2",Sample(11,11)},{"n3",Sample(12,12)}};
            for(int i=0;i<40;i++) { var batch=Planner.Batch(candidates,"n0",records,i,12);Check(batch.Count<=12&&batch.Contains("n0"),"bounded batch retains current "+i);covered.UnionWith(batch); }
            Check(covered.Count==65,"rotating coverage visits all nodes");
            var snapshot=new Snapshot{Mode="rule"};snapshot.Proxies["g"]=new ProxyInfo{Name="g",Type="Selector",Now="n0",Members=new List<string>{"DIRECT","n0","auto"}};
            snapshot.Proxies["DIRECT"]=new ProxyInfo{Name="DIRECT",Type="Direct"};snapshot.Proxies["n0"]=new ProxyInfo{Name="n0",Type="VMess"};snapshot.Proxies["auto"]=new ProxyInfo{Name="auto",Type="URLTest",Members=new List<string>{"n0"}};
            Check(snapshot.Candidates("g").SequenceEqual(new[]{"n0"}),"direct and nested groups excluded");
            string fingerprint=snapshot.Fingerprint("g");snapshot.Proxies["g"].Now="auto";Check(fingerprint==snapshot.Fingerprint("g"),"selection is tracked separately from membership");
            Check(snapshot.TestTargets("g").SequenceEqual(new[]{"n0","auto"}),"current nested group is included as measured baseline");
            Check(snapshot.Candidates("g").SequenceEqual(new[]{"n0"}),"nested baseline is not a switch candidate");
            Check(snapshot.CanRestore("g","auto"),"original nested group is restorable");
            Check(!snapshot.CanRestore("g","DIRECT"),"direct selection is not restorable by automatic tool");
            snapshot.Proxies["reject"]=new ProxyInfo{Name="reject",Type="Reject"};snapshot.Proxies["g"].Members.Add("reject");
            Check(!snapshot.CanRestore("g","reject"),"reject selection is not restorable by automatic tool");
            snapshot.Proxies["otherAuto"]=new ProxyInfo{Name="otherAuto",Type="URLTest",Members=new List<string>{"n0"}};snapshot.Proxies["g"].Members.Add("otherAuto");
            Check(!snapshot.TestTargets("g").Contains("otherAuto"),"noncurrent nested group is never probed as candidate");
            var nestedSamples=new Dictionary<string,Samples>{{"auto",Sample(300,300)},{"n0",Sample(100,100)},{"otherAuto",Sample(1,1)}};
            var nestedBatch=Planner.Batch(snapshot.TestTargets("g"),"auto",nestedSamples,0,12);
            Check(nestedBatch.Contains("auto")&&!nestedBatch.Contains("otherAuto"),"planner pins baseline and excludes unrelated nested groups");
            var nestedPolicy=new Policy();var nestedFresh=new HashSet<string>(nestedBatch);
            Check(!nestedPolicy.Choose("auto",nestedSamples,nestedFresh,now,DateTime.MinValue).Switch,"nested baseline receives two-round protection");
            var nestedChoice=nestedPolicy.Choose("auto",nestedSamples,nestedFresh,now,DateTime.MinValue);
            Check(nestedChoice.Switch&&nestedChoice.Candidate=="n0","nested baseline can switch only to valid direct candidate");
            snapshot.Proxies["g"].Now="n0";
            Check(snapshot.TestTargets("g").SequenceEqual(new[]{"n0"}),"old nested group is removed from sampling after switch");
            Check(snapshot.CanRestore("g","auto"),"original nested group remains restorable after node switch");
            snapshot.Proxies["g"].Members.Remove("auto");Check(!snapshot.CanRestore("g","auto"),"removed original nested group cannot be restored");
            snapshot.Proxies["g"].Members.Add("g");snapshot.Proxies["g"].Now="g";
            Check(!snapshot.TestTargets("g").Contains("g")&&!snapshot.CanRestore("g","g"),"self-referential group rejected");
            snapshot.Mode="direct";Check(fingerprint!=snapshot.Fingerprint("g"),"mode change changes fingerprint");
            using(var form=new MainForm())
            {
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var apiField=typeof(MainForm).GetField("api",flags);
                var groupField=typeof(MainForm).GetField("group",flags);
                var addressField=typeof(MainForm).GetField("address",flags);
                var passwordField=typeof(MainForm).GetField("password",flags);
                apiField.SetValue(form,new Controller("http://127.0.0.1:9090",""));
                ((ComboBox)groupField.GetValue(form)).Items.Add("stale");
                ((TextBox)addressField.GetValue(form)).Text="http://127.0.0.1:9091";
                Check(apiField.GetValue(form)==null&&((ComboBox)groupField.GetValue(form)).Items.Count==0,"address edit discards old controller and groups");
                apiField.SetValue(form,new Controller("http://127.0.0.1:9090",""));
                ((TextBox)passwordField.GetValue(form)).Text="TEST_ONLY";
                Check(apiField.GetValue(form)==null,"credential edit discards old controller");
                var clock=(System.Windows.Forms.Timer)typeof(MainForm).GetField("clock",flags).GetValue(form);clock.Stop();
                var groupControl=(ComboBox)groupField.GetValue(form);groupControl.Items.Add("g");groupControl.SelectedIndex=0;
                apiField.SetValue(form,new Controller("http://127.0.0.1:9090",""));
                typeof(MainForm).GetMethod("Buttons",flags).Invoke(form,null);
                typeof(MainForm).GetField("trafficBusy",flags).SetValue(form,true);
                var trafficTask=(System.Threading.Tasks.Task)typeof(MainForm).GetMethod("UpdateTraffic",flags).Invoke(form,null);
                Check(trafficTask.IsCompleted&&groupControl.Enabled,"overlapping traffic polling exits without disabling group picker");
                Check(!(bool)typeof(MainForm).GetField("busy",flags).GetValue(form),"traffic polling does not acquire main-operation busy state");
            }
            // Network tests are console operations; do not capture a WinForms context without its message loop.
            SynchronizationContext.SetSynchronizationContext(null);
            if(args.Length>0)
            {
                var api=new Controller(args[0],"local-test-secret");
                var remote=api.Read(CancellationToken.None).GetAwaiter().GetResult();
                Check(remote.Mode=="rule"&&remote.Candidates("代理 / #组").Contains("香港 / A #1"),"mock API read unicode nodes");
                int? delay=api.Delay("香港 / A #1","https://www.gstatic.com/generate_204",CancellationToken.None).GetAwaiter().GetResult();
                Check(delay==125,"encoded node delay route");
                Check(api.Delay("timeout","https://www.gstatic.com/generate_204",CancellationToken.None).GetAwaiter().GetResult()==null,"failed delay represented as missing");
                bool wrong=false;try{new Controller(args[0],"wrong").Read(CancellationToken.None).GetAwaiter().GetResult();}catch(PilotException){wrong=true;}Check(wrong,"authentication error fails closed");
                awaitSelection(api);
                var totals=api.Totals(CancellationToken.None).GetAwaiter().GetResult();Check(totals[0]>0,"read-only traffic aggregate");
                var cancellation=new CancellationTokenSource(); cancellation.Cancel(); bool cancelled=false;
                try{api.Select("代理 / #组","香港 / A #1",cancellation.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancelled selection never sent");
            }
            Console.WriteLine("ALL "+checks+" CHECKS PASSED");return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error);return 1; }
    }
    static void awaitSelection(Controller api)
    {
        api.Select("代理 / #组","香港 / A #1",CancellationToken.None).GetAwaiter().GetResult();
        var read=api.Read(CancellationToken.None).GetAwaiter().GetResult();Check(read.Proxies["代理 / #组"].Now=="香港 / A #1","select and verify exact member");
    }
}
