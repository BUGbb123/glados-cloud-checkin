using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace FlClashPilot
{
    public class PilotException : Exception { public PilotException(string message) : base(message) {} }

    public class ProxyInfo
    {
        public string Name, Type, Now;
        public List<string> Members = new List<string>();
        public bool IsGroup { get { return Members.Count > 0 || new[] {"Selector", "URLTest", "Fallback", "LoadBalance", "Relay"}.Contains(Type); } }
        public bool IsNode { get { return !IsGroup && !new[] {"direct", "reject", "rejectdrop", "pass", "compatible", "dns"}.Contains((Type ?? "").ToLowerInvariant()); } }
    }

    public class Snapshot
    {
        public string Mode;
        public Dictionary<string, ProxyInfo> Proxies = new Dictionary<string, ProxyInfo>();
        public List<string> Candidates(string group)
        {
            if (!Proxies.ContainsKey(group) || Proxies[group].Type != "Selector") throw new PilotException("该策略组不可手动切换，请选择“Selector / 手动选择”组。");
            return Proxies[group].Members.Where(n => Proxies.ContainsKey(n) && Proxies[n].IsNode).Distinct().ToList();
        }
        public bool CanRestore(string group, string member)
        {
            return group != member && Proxies.ContainsKey(group) && Proxies[group].Type == "Selector"
                && Proxies[group].Members.Contains(member) && Proxies.ContainsKey(member)
                && (Proxies[member].IsNode || Proxies[member].IsGroup);
        }
        public List<string> TestTargets(string group)
        {
            var targets = Candidates(group);
            var current = Proxies[group].Now;
            // An already-selected nested group is a baseline only. Other nested groups
            // are never introduced as automatic switch candidates.
            if (CanRestore(group, current) && Proxies[current].IsGroup && !targets.Contains(current)) targets.Add(current);
            return targets;
        }
        public string Fingerprint(string group)
        {
            if (!Proxies.ContainsKey(group)) return "missing";
            // Membership and types, not traffic or the selected node. No subscription content is read.
            return Mode + "\n" + String.Join("\n", Proxies[group].Members.OrderBy(n => n, StringComparer.Ordinal).Select(n => n + "\t" + (Proxies.ContainsKey(n) ? Proxies[n].Type : "missing")));
        }
    }

    public interface IController
    {
        Task<Snapshot> Read(CancellationToken token);
        Task<int?> Delay(string node, string url, CancellationToken token);
        Task Select(string group, string node, CancellationToken token);
    }

    public class Controller : IController
    {
        public readonly string Address;
        private readonly string secret;
        public Controller(string address, string password) { Address = ValidateAddress(address); secret = password ?? ""; }
        public static string ValidateAddress(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "http" || uri.Port <= 0 || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
                throw new PilotException("控制地址应为 http://127.0.0.1:9090，只支持本机 HTTP 控制接口。");
            if (uri.Host != "127.0.0.1" && uri.Host != "localhost" && uri.Host != "[::1]" && uri.Host != "::1")
                throw new PilotException("为保护控制口令，只允许连接本机回环地址。");
            string host = (uri.Host == "[::1]" || uri.Host == "::1") ? "[::1]" : "127.0.0.1";
            return "http://" + host + ":" + uri.Port;
        }
        public static string ValidateTestUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo) || !String.IsNullOrEmpty(uri.Fragment))
                throw new PilotException("检测地址必须是无用户名、密码和片段的 HTTPS 地址。");
            return uri.AbsoluteUri;
        }
        private async Task<Dictionary<string, object>> Request(string method, string path, object body, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(Address + path);
            request.Method = method; request.Proxy = null; request.AllowAutoRedirect = false;
            request.Timeout = 7000; request.ReadWriteTimeout = 7000;
            if (secret.Length > 0) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + secret;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(8000);
                using (deadline.Token.Register(() => request.Abort()))
                {
                    try
                    {
                        if (body != null)
                        {
                            byte[] bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(body));
                            request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                            using (var output = await request.GetRequestStreamAsync().ConfigureAwait(false)) await output.WriteAsync(bytes, 0, bytes.Length, deadline.Token).ConfigureAwait(false);
                        }
                        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                        {
                            if ((int)response.StatusCode >= 300) throw new PilotException("控制接口返回重定向，已拒绝跟随。");
                            using (var input = response.GetResponseStream())
                            using (var memory = new MemoryStream())
                            {
                                var buffer = new byte[8192]; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, deadline.Token).ConfigureAwait(false)) > 0)
                                {
                                    memory.Write(buffer, 0, count);
                                    if (memory.Length > 4 * 1024 * 1024) throw new PilotException("控制接口数据过大，已停止读取。");
                                }
                                var json = Encoding.UTF8.GetString(memory.ToArray());
                                if (json.Length == 0) return new Dictionary<string, object>();
                                return new JavaScriptSerializer {MaxJsonLength = 4 * 1024 * 1024}.Deserialize<Dictionary<string, object>>(json);
                            }
                        }
                    }
                    catch (WebException error)
                    {
                        token.ThrowIfCancellationRequested();
                        var response = error.Response as HttpWebResponse;
                        if (response != null)
                        {
                            int status = (int)response.StatusCode; response.Dispose();
                            if (status == 401 || status == 403) throw new PilotException("控制口令不匹配，请只在本窗口输入本机控制口令。");
                            if (status == 404) throw new PilotException("节点或接口已变化，请重新连接。");
                            if (path.EndsWith("/delay", StringComparison.Ordinal) || path.Contains("/delay?")) return null;
                            throw new PilotException("控制接口请求失败（HTTP " + status + "）。");
                        }
                        if (deadline.IsCancellationRequested) throw new PilotException("本机接口响应超时，请确认 FlClash 正在运行。");
                        throw new PilotException("无法连接本机控制接口，请在 FlClash 中启用外部控制器并核对端口。");
                    }
                    catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new PilotException("本机接口响应超时。"); }
                }
            }
        }
        public async Task<Snapshot> Read(CancellationToken token)
        {
            var raw = await Request("GET", "/proxies", null, token);
            var config = await Request("GET", "/configs", null, token);
            var snapshot = new Snapshot {Mode = Text(config, "mode")};
            var proxies = raw != null && raw.ContainsKey("proxies") ? raw["proxies"] as Dictionary<string, object> : null;
            if (proxies == null) throw new PilotException("接口没有返回有效的策略组。");
            foreach (var pair in proxies)
            {
                var item = pair.Value as Dictionary<string, object>; if (item == null) continue;
                var proxy = new ProxyInfo {Name=pair.Key, Type=Text(item,"type"), Now=Text(item,"now")};
                if (item.ContainsKey("all") && item["all"] is IEnumerable) foreach (object value in (IEnumerable)item["all"]) if (value is string) proxy.Members.Add((string)value);
                snapshot.Proxies.Add(pair.Key, proxy);
            }
            return snapshot;
        }
        internal static string Text(Dictionary<string, object> item, string key) { return item.ContainsKey(key) ? Convert.ToString(item[key], CultureInfo.InvariantCulture) : ""; }
        public async Task<int?> Delay(string node, string url, CancellationToken token)
        {
            var result = await Request("GET", "/proxies/" + Uri.EscapeDataString(node) + "/delay?timeout=4000&expected=204&url=" + Uri.EscapeDataString(ValidateTestUrl(url)), null, token);
            int delay;
            return result != null && result.ContainsKey("delay") && Int32.TryParse(Convert.ToString(result["delay"]), out delay) && delay > 0 && delay < 60000 ? (int?)delay : null;
        }
        public async Task Select(string group, string node, CancellationToken token)
        {
            await Request("PUT", "/proxies/" + Uri.EscapeDataString(group), new Dictionary<string, string> {{"name", node}}, token);
        }
        public async Task<long[]> Totals(CancellationToken token)
        {
            // Only totals are displayed; connection destinations and process information are neither logged nor retained.
            var data = await Request("GET", "/connections", null, token);
            return new[] {Convert.ToInt64(data["downloadTotal"]), Convert.ToInt64(data["uploadTotal"])};
        }
    }

    public class Samples
    {
        public readonly Queue<int?> Values = new Queue<int?>();
        public int ConsecutiveFailures;
        public void Add(int? value) { Values.Enqueue(value); while (Values.Count > 5) Values.Dequeue(); ConsecutiveFailures = value.HasValue ? 0 : ConsecutiveFailures + 1; }
        public int Successes { get { return Values.Count(x => x.HasValue); } }
        public double FailureRate { get { return Values.Count == 0 ? 1 : (double)(Values.Count - Successes) / Values.Count; } }
        public double Median { get { var list = Values.Where(x => x.HasValue).Select(x => (double)x.Value).OrderBy(x => x).ToArray(); return list.Length == 0 ? Double.PositiveInfinity : (list[(list.Length-1)/2] + list[list.Length/2])/2; } }
        public double Jitter { get { var list = Values.Where(x => x.HasValue).Select(x => (double)x.Value).ToArray(); return list.Length < 2 ? 0 : list.Average(x => Math.Abs(x - Median)); } }
        public double Score { get { return Median + Jitter * 0.5 + FailureRate * 2000; } }
        public bool Eligible { get { return Successes >= 2 && FailureRate <= 0.4 && ConsecutiveFailures == 0; } }
    }

    public class Decision { public string Candidate, Reason; public bool Switch; }

    public class Policy
    {
        private string winning = ""; private int wins;
        public Decision Choose(string current, Dictionary<string, Samples> samples, HashSet<string> fresh, DateTime now, DateTime lastSwitch)
        {
            var best = samples.Where(p => p.Value.Eligible && fresh.Contains(p.Key)).OrderBy(p => p.Value.Score).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
            if (best.Key == null) { winning=""; wins=0; return new Decision {Reason="尚无足够的成功样本，保留当前线路。"}; }
            if (best.Key == current) { winning=""; wins=0; return new Decision {Candidate=current, Reason="当前线路综合表现较好，继续保持。"}; }
            wins = winning == best.Key ? wins + 1 : 1; winning = best.Key;
            Samples old;
            bool oldKnown = samples.TryGetValue(current ?? "", out old);
            if (oldKnown && old.ConsecutiveFailures >= 2) return new Decision {Switch=true, Candidate=best.Key, Reason="当前线路连续两次失败，切换到已有成功样本的线路。"};
            if (!oldKnown || !fresh.Contains(current) || !old.Values.Last().HasValue) return new Decision {Candidate=best.Key, Reason="继续核对当前线路，避免一次失败就切换。"};
            if ((now-lastSwitch).TotalSeconds < 180) return new Decision {Candidate=best.Key, Reason="处于 3 分钟切换冷却期。"};
            if (old.Score - best.Value.Score < Math.Max(50, old.Score * 0.2)) return new Decision {Candidate=best.Key, Reason="改善不明显，保留当前线路。"};
            if (wins < 2) return new Decision {Candidate=best.Key, Reason="发现更好候选，再确认一轮后切换。"};
            winning=""; wins=0;
            return new Decision {Switch=true, Candidate=best.Key, Reason="候选连续两轮领先，且综合改善达到阈值。"};
        }
    }

    public static class Planner
    {
        public static List<string> Batch(List<string> candidates, string current, Dictionary<string, Samples> samples, int round, int limit)
        {
            var result = new List<string>();
            Action<string> add = n => { if (candidates.Contains(n) && !result.Contains(n) && result.Count < limit) result.Add(n); };
            add(current);
            foreach (var p in samples.Where(p => candidates.Contains(p.Key) && p.Value.Eligible).OrderBy(p => p.Value.Score).Take(3)) add(p.Key);
            // The rotating window advances independently of the best/current nodes, so every candidate gets tested.
            for (int i=0; i<candidates.Count && result.Count<limit; i++) add(candidates[(round * Math.Max(1,limit-4) + i) % candidates.Count]);
            return result;
        }
    }

    public class MainForm : Form
    {
        readonly TextBox address = new TextBox {Text="http://127.0.0.1:9090", Width=245};
        readonly TextBox password = new TextBox {UseSystemPasswordChar=true, Width=245};
        readonly ComboBox group = new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList, Width=375};
        readonly NumericUpDown interval = new NumericUpDown {Minimum=30, Maximum=600, Value=60, Width=75};
        readonly Button connect = new Button {Text="连接 / 刷新策略组", AutoSize=true};
        readonly Button measure = new Button {Text="只检测一次", AutoSize=true};
        readonly Button start = new Button {Text="开始自动选线", AutoSize=true};
        readonly Button pause = new Button {Text="暂停自动", AutoSize=true};
        readonly Button restore = new Button {Text="恢复接管前选择", AutoSize=true};
        readonly Label status = new Label {Dock=DockStyle.Fill, AutoEllipsis=true, Text="尚未连接 · 自动选线未启动", ForeColor=Color.FromArgb(27,88,144)};
        readonly Label traffic = new Label {AutoSize=true, Text="实际流量：连接后显示（不是线路带宽上限）"};
        readonly ListView table = new ListView {Dock=DockStyle.Fill, View=View.Details, FullRowSelect=true, GridLines=false, AccessibleName="节点检测结果"};
        readonly TextBox log = new TextBox {Dock=DockStyle.Fill, Multiline=true, ReadOnly=true, ScrollBars=ScrollBars.Vertical, AccessibleName="运行日志"};
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer {Interval=1000};
        Controller api;
        Snapshot snapshot;
        Dictionary<string,Samples> samples = new Dictionary<string,Samples>();
        Policy policy = new Policy();
        CancellationTokenSource operation;
        bool busy, trafficBusy, automatic, closing;
        string ownedGroup, original, expected, fingerprint, measuredGroup;
        DateTime lastSwitch=DateTime.MinValue, nextRound=DateTime.MaxValue, lastTrafficTime=DateTime.MinValue;
        long[] previousTotals;
        int round, apiFailures;
        public MainForm()
        {
            Text="FlClash Pilot · 本机自动选线 v1.1"; StartPosition=FormStartPosition.CenterScreen;
            Size=new Size(1040,780); MinimumSize=new Size(900,680); AutoScaleMode=AutoScaleMode.Dpi;
            Font=new Font("Microsoft YaHei UI",10); BackColor=Color.FromArgb(245,248,252);
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill, Padding=new Padding(22), ColumnCount=1, RowCount=9};
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,45));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,47));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,47));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,65));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,38));
            layout.Controls.Add(new Label {Text="自动挑选响应更快、更稳定的线路", Font=new Font(Font.FontFamily,17,FontStyle.Bold), AutoSize=true},0,0);
            layout.Controls.Add(Row("本机控制地址",address,"控制口令",password,connect),0,1);
            layout.Controls.Add(Row("托管策略组",group,"每轮间隔（秒）",interval),0,2);
            layout.Controls.Add(Row(measure,start,pause,restore),0,3);
            layout.Controls.Add(status,0,4); layout.Controls.Add(traffic,0,5);
            table.Columns.Add("节点",355); table.Columns.Add("中位延迟",115); table.Columns.Add("波动",90); table.Columns.Add("近 5 次失败",120); table.Columns.Add("状态",210);
            layout.Controls.Add(table,0,6);
            layout.Controls.Add(new Label {AutoSize=true, Text="每轮最多检测 12 个节点，逐轮覆盖全部候选。按延迟、波动、失败率评分；不等同于下载带宽测试。\n只托管所选组的直接节点；不自动修改规则、DNS、订阅或已有连接。关闭窗口即停止自动选线。", ForeColor=Color.DimGray},0,7);
            layout.Controls.Add(log,0,8); Controls.Add(layout);
            connect.Click += async (s,e) => await Run(Connect);
            measure.Click += async (s,e) => await Run(async token => await Measure(false,token));
            start.Click += async (s,e) => await Run(Start);
            pause.Click += (s,e) => Stop("已暂停自动选线，保留当前节点。");
            restore.Click += async (s,e) => { Stop("准备恢复接管前选择。"); await Run(Restore); };
            group.SelectedIndexChanged += (s,e) => { if (!busy) { samples.Clear(); policy=new Policy(); round=0; measuredGroup=null; PaintRows(); } };
            address.TextChanged += (s,e) => ConnectionEdited();
            password.TextChanged += (s,e) => ConnectionEdited();
            clock.Tick += async (s,e) => {
                if (busy || closing || api==null) return;
                if (automatic && DateTime.UtcNow>=nextRound) await Run(async token => await Measure(true,token));
                else if (!trafficBusy && (DateTime.UtcNow-lastTrafficTime).TotalSeconds>=5) await UpdateTraffic();
            };
            FormClosing += (s,e) => { closing=true; automatic=false; clock.Stop(); if(operation!=null)operation.Cancel(); };
            clock.Start(); Buttons();
        }
        static Control Row(params object[] items)
        {
            var row=new FlowLayoutPanel {Dock=DockStyle.Fill, WrapContents=false};
            foreach (var item in items) { var control=item as Control ?? new Label {Text=Convert.ToString(item),AutoSize=true,Padding=new Padding(0,6,4,0)}; control.Margin=new Padding(0,2,10,2); row.Controls.Add(control); }
            return row;
        }
        void Note(string message) { if(closing)return; status.Text=message; log.AppendText(DateTime.Now.ToString("HH:mm:ss")+"  "+message+Environment.NewLine); if(log.TextLength>14000)log.Text=log.Text.Substring(log.TextLength-10000); }
        void Buttons()
        {
            connect.Enabled=!busy&&!automatic; address.Enabled=password.Enabled=!busy&&!automatic;
            group.Enabled=interval.Enabled=!busy&&!automatic;
            start.Enabled=measure.Enabled=!busy&&!automatic&&api!=null&&group.SelectedIndex>=0;
            pause.Enabled=automatic||busy; restore.Enabled=!busy&&api!=null&&original!=null;
        }
        void Stop(string reason) { automatic=false; nextRound=DateTime.MaxValue; if(operation!=null)operation.Cancel(); Note(reason); Buttons(); }
        async Task Run(Func<CancellationToken,Task> work)
        {
            if(busy||closing)return; busy=true; operation=new CancellationTokenSource(); Buttons();
            try { await work(operation.Token); }
            catch(OperationCanceledException) { if(!closing)Note("本轮已停止；如已发出切换请求，请刷新以确认当前节点。"); }
            catch(Exception error) { if(!closing) { automatic=false; Note(error is PilotException ? error.Message : "操作未完成，请重新连接；未记录控制口令或接口响应。"); } }
            finally { busy=false; operation.Dispose(); operation=null; if(!closing)Buttons(); }
        }
        async Task Connect(CancellationToken token)
        {
            ForgetConnection();
            var nextApi=new Controller(address.Text.Trim(),password.Text);
            var nextSnapshot=await nextApi.Read(token);
            api=nextApi; snapshot=nextSnapshot; samples.Clear(); policy=new Policy(); round=0; measuredGroup=null;
            original=ownedGroup=expected=fingerprint=null; previousTotals=null;
            group.Items.Clear(); foreach(var item in snapshot.Proxies.Values.Where(p=>p.Type=="Selector"&&snapshot.Candidates(p.Name).Count>0).OrderBy(p=>p.Name,StringComparer.Ordinal))group.Items.Add(item.Name);
            if(group.Items.Count>0)group.SelectedIndex=0;
            Note("已连接 · 当前模式："+snapshot.Mode+" · 可托管组 "+group.Items.Count+" 个。请选中实际使用的策略组。");
            PaintRows();
        }
        void ForgetConnection()
        {
            automatic=false; nextRound=DateTime.MaxValue;
            api=null; snapshot=null; group.Items.Clear();
            original=ownedGroup=expected=fingerprint=measuredGroup=null;
            samples.Clear(); policy=new Policy(); round=0; previousTotals=null;
            table.Items.Clear(); traffic.Text="实际流量：连接后显示（不是线路带宽上限）";
            Buttons();
        }
        void ConnectionEdited()
        {
            if(api==null || busy || automatic)return;
            ForgetConnection();
            Note("连接信息已修改，旧接口已断开；请重新连接并选择策略组。");
        }
        async Task Start(CancellationToken token)
        {
            string selected=Convert.ToString(group.SelectedItem);
            var fresh=await api.Read(token);
            if(fresh.Mode=="direct")throw new PilotException("当前为直连模式，自动选线不会生效；请先在 FlClash 中选择规则模式。");
            if(fresh.Mode=="global" && selected!="GLOBAL")throw new PilotException("全局模式只会使用 GLOBAL 组，请先改选 GLOBAL，或在 FlClash 中使用规则模式。");
            var nodes=fresh.Candidates(selected); if(nodes.Count==0)throw new PilotException("该组没有可托管的直接节点。");
            if(!fresh.TestTargets(selected).Contains(fresh.Proxies[selected].Now))throw new PilotException("当前选择为直连、拒绝或不可识别的选项，未启动托管；请先选择代理节点或有效策略组。");
            if(original!=null && (ownedGroup!=selected || fingerprint!=fresh.Fingerprint(selected)))throw new PilotException("上次恢复点属于其他组或配置；请先连接 / 刷新，再建立新的恢复点。");
            if(original==null) { original=fresh.Proxies[selected].Now; ownedGroup=selected; fingerprint=fresh.Fingerprint(selected); }
            expected=fresh.Proxies[selected].Now; samples.Clear(); policy=new Policy(); round=0; lastSwitch=DateTime.MinValue; apiFailures=0;
            automatic=true; Note("已开始托管；手动换线或可检测的组成员变化将暂停自动选线。");
            await Measure(true,token);
        }
        async Task Measure(bool managed, CancellationToken token)
        {
            string selected=managed?ownedGroup:Convert.ToString(group.SelectedItem);
            Snapshot before;
            try { before=await api.Read(token); apiFailures=0; }
            catch(PilotException) { if(managed && ++apiFailures<3) { nextRound=DateTime.UtcNow.AddSeconds((int)interval.Value); Note("本机控制接口暂时不可用；本轮不切换，稍后重试。"); return; } throw; }
            if(managed && (before.Fingerprint(selected)!=fingerprint || before.Proxies[selected].Now!=expected)) { automatic=false; Note("检测到手动选择或策略组变化，已暂停，请确认后重新连接。"); return; }
            if(measuredGroup!=selected) { samples.Clear(); measuredGroup=selected; round=0; }
            snapshot=before;
            var nodes=before.TestTargets(selected); var current=before.Proxies[selected].Now;
            var batch=Planner.Batch(nodes,current,samples,round++,12);
            Note("检测中 · 本轮 "+batch.Count+" / "+nodes.Count+" 个候选（仅发出小型连通性请求）…");
            var measured=new Dictionary<string,int?>();
            using(var gate=new SemaphoreSlim(3))
            {
                await Task.WhenAll(batch.Select(async name => {
                    await gate.WaitAsync(token);
                    try { var value=await api.Delay(name,"https://www.gstatic.com/generate_204",token); lock(measured)measured[name]=value; }
                    finally { gate.Release(); }
                }));
            }
            token.ThrowIfCancellationRequested();
            foreach(var pair in measured) { if(!samples.ContainsKey(pair.Key))samples[pair.Key]=new Samples(); samples[pair.Key].Add(pair.Value); }
            var fresh=await api.Read(token); snapshot=fresh;
            if(managed && (fresh.Fingerprint(selected)!=fingerprint || fresh.Proxies[selected].Now!=expected)) { automatic=false; Note("检测期间配置或选择发生变化，已暂停，本轮不切换。"); PaintRows(); return; }
            var choice=policy.Choose(current,samples,new HashSet<string>(batch),DateTime.UtcNow,lastSwitch);
            if(managed && automatic && choice.Switch)
            {
                token.ThrowIfCancellationRequested();
                if(!fresh.Candidates(selected).Contains(choice.Candidate))throw new PilotException("候选节点已不在策略组中，已暂停。");
                // The only mutation this application performs is selecting one member of one Selector.
                await api.Select(selected,choice.Candidate,token);
                var verified=await api.Read(token);
                if(verified.Fingerprint(selected)!=fingerprint || verified.Proxies[selected].Now!=choice.Candidate)throw new PilotException("切换后校验未通过，已停止自动操作；请在 FlClash 查看当前选择。");
                expected=choice.Candidate; lastSwitch=DateTime.UtcNow; snapshot=verified;
                Note("已切换到 "+expected+" · "+choice.Reason);
            }
            else Note((managed?"自动选线 · ":"只检测 · ")+choice.Reason+(managed?"":" 本轮没有更改节点。"));
            if(managed&&automatic)nextRound=DateTime.UtcNow.AddSeconds((int)interval.Value);
            PaintRows();
        }
        async Task Restore(CancellationToken token)
        {
            if(original==null)return;
            var fresh=await api.Read(token);
            if(fresh.Fingerprint(ownedGroup)!=fingerprint || !fresh.Proxies.ContainsKey(ownedGroup) || fresh.Proxies[ownedGroup].Now!=expected || !fresh.CanRestore(ownedGroup,original))
                throw new PilotException("配置或手动选择已变化，为避免覆盖你的操作，未自动恢复；请在 FlClash 中手动选择原节点。");
            await api.Select(ownedGroup,original,token);
            var verified=await api.Read(token);
            if(verified.Fingerprint(ownedGroup)!=fingerprint || verified.Proxies[ownedGroup].Now!=original)throw new PilotException("恢复后校验未通过，请在 FlClash 中查看。");
            expected=original; snapshot=verified; Note("已恢复接管前选择："+original); PaintRows();
        }
        void PaintRows()
        {
            table.BeginUpdate(); table.Items.Clear();
            string selected=Convert.ToString(group.SelectedItem);
            if(snapshot!=null&&snapshot.Proxies.ContainsKey(selected))
            {
                var current=snapshot.Proxies[selected].Now;
                foreach(var name in snapshot.TestTargets(selected).OrderBy(n=>samples.ContainsKey(n)?samples[n].Score:Double.PositiveInfinity))
                {
                    Samples data; samples.TryGetValue(name,out data);
                    var row=new ListViewItem(name);
                    row.SubItems.Add(data==null||data.Successes==0?"—":Math.Round(data.Median)+" ms");
                    row.SubItems.Add(data==null||data.Successes==0?"—":Math.Round(data.Jitter)+" ms");
                    row.SubItems.Add(data==null?"未检测":(data.Values.Count-data.Successes)+" / "+data.Values.Count);
                    row.SubItems.Add(name==current?(snapshot.Proxies[name].IsGroup?"当前选择 · 策略组":"当前选择"):data==null?"等待轮测":data.Eligible?"可用候选":"继续观察");
                    if(name==current) { row.ForeColor=Color.FromArgb(20,115,92); row.Font=new Font(table.Font,FontStyle.Bold); }
                    table.Items.Add(row);
                }
            }
            table.EndUpdate();
        }
        async Task UpdateTraffic()
        {
            if(busy || trafficBusy || closing || api==null)return;
            trafficBusy=true; var trafficApi=api; var time=DateTime.UtcNow;
            try
            {
                var totals=await trafficApi.Totals(CancellationToken.None);
                if(closing || !Object.ReferenceEquals(api,trafficApi))return;
                if(previousTotals!=null) { double seconds=(time-lastTrafficTime).TotalSeconds; if(seconds>0&&totals[0]>=previousTotals[0]&&totals[1]>=previousTotals[1])traffic.Text="实际下载 "+Rate((totals[0]-previousTotals[0])/seconds)+"     上传 "+Rate((totals[1]-previousTotals[1])/seconds)+"     （当前使用量，不是线路带宽上限）"; }
                previousTotals=totals;
            }
            catch { if(!closing && Object.ReferenceEquals(api,trafficApi))traffic.Text="实际流量暂不可用；不根据空闲流量判定线路慢。"; }
            finally { if(Object.ReferenceEquals(api,trafficApi))lastTrafficTime=time; trafficBusy=false; }
        }
        static string Rate(double bytes) { return bytes>=1048576?(bytes/1048576).ToString("F2")+" MB/s":(bytes/1024).ToString("F1")+" KB/s"; }
    }

    public static class Program
    {
        [STAThread] public static void Main()
        {
            bool created;
            using(var singleton=new Mutex(true,"Local\\FlClashPilot.SingleInstance.v1",out created))
            {
                if(!created) { MessageBox.Show("自动选线工具已经运行，请使用已打开的窗口。","FlClash Pilot"); return; }
                ServicePointManager.DefaultConnectionLimit=12;
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Application.Run(new MainForm());
            }
        }
    }
}
