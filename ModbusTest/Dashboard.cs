using System.Text;

namespace ModbusTest;

/// <summary>
/// 可视化仪表盘:顶部为串口状态与收发统计,中部为实时日志,
/// 底部为命令输入行。启动后自动进入,主线程负责渲染与按键处理。
/// </summary>
public class Dashboard
{
    private readonly SerialMonitor _monitor;
    private readonly StringBuilder _input = new();
    private bool _dirty = true;
    private bool _running = true;

    public Dashboard(SerialMonitor monitor)
    {
        _monitor = monitor;
        // 数据/日志变化时标记界面为脏,下一轮渲染
        _monitor.Changed += () => _dirty = true;
    }

    public void Run()
    {
        // 输入/输出被重定向(管道、日志采集)时,光标与按键 API 不可用,降级为简易命令行模式
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            RunSimpleMode();
            return;
        }

        Console.CursorVisible = false;
        try
        {
            while (_running)
            {
                if (_dirty) { Render(); _dirty = false; }
                if (Console.KeyAvailable) HandleKey(Console.ReadKey(true));
                Thread.Sleep(20);
            }
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }

    /// <summary>非交互式控制台下的降级模式:逐行读取命令并执行,日志直接打印。</summary>
    private void RunSimpleMode()
    {
        Console.WriteLine("简易模式(非交互式控制台): 输入 help 查看命令,exit 退出");
        int last = PrintLogs(0);
        _monitor.Changed += () => last = PrintLogs(last);
        while (_running)
        {
            string? line = Console.ReadLine();
            if (line is null) break; // 输入流结束
            Execute(line.Trim());
        }
    }

    /// <summary>打印从 last 到末尾的新增日志,返回当前日志条数。</summary>
    private int PrintLogs(int last)
    {
        var logs = _monitor.GetLogs();
        if (logs.Count < last) last = 0; // 日志被清空过
        for (int i = last; i < logs.Count; i++)
        {
            var e = logs[i];
            Console.WriteLine($"[{e.Time:HH:mm:ss.fff}] {e.Source} {e.Direction} {FormatContent(e.Content)}");
        }
        return logs.Count;
    }

    // ── 按键处理 ──────────────────────────────────────────────

    private void HandleKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Execute(_input.ToString().Trim());
                _input.Clear();
                _dirty = true;
                break;
            case ConsoleKey.Backspace:
                if (_input.Length > 0) _input.Length--;
                _dirty = true;
                break;
            case ConsoleKey.Escape:
                // 输入为空时退出;非空时先清空输入
                if (_input.Length > 0) { _input.Clear(); _dirty = true; }
                else _running = false;
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                {
                    _input.Append(key.KeyChar);
                    _dirty = true;
                }
                break;
        }
    }

    // ── 命令执行 ──────────────────────────────────────────────

    private void Execute(string cmd)
    {
        if (cmd.Length == 0) return;

        if (cmd is "help" or "?" or "/?") { ShowHelp(); return; }
        if (cmd is "clear" or "cls") { _monitor.ClearLogs(); return; }
        if (cmd is "exit" or "quit") { _running = false; return; }

        if (cmd.StartsWith("wait ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(cmd["wait ".Length..].Trim(), out int waitMs))
        {
            Thread.Sleep(Math.Clamp(waitMs, 0, 60_000));
            return;
        }

        // 注意顺序:sendhex 前缀更长,必须先于 send 匹配,否则 "11sendhex ..." 会被当作文本命令
        if (TrySendHexCommand(cmd, "10sendhex", _monitor.Host)) return;
        if (TrySendHexCommand(cmd, "11sendhex", _monitor.Slave)) return;
        if (TrySendCommand(cmd, "10send", _monitor.Host)) return;
        if (TrySendCommand(cmd, "11send", _monitor.Slave)) return;

        _monitor.Notify($"未识别的命令: {cmd}(输入 help 查看帮助)");
    }

    private bool TrySendCommand(string cmd, string prefix, SerialChannel ch)
    {
        if (!cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        // 取前缀之后的完整内容作为数据,不再做脆弱的字符串分割
        string data = cmd[prefix.Length..];
        if (data.Length == 0)
        {
            _monitor.Notify($"{prefix} 缺少数据,用法: {prefix}<数据>");
            return true;
        }
        _monitor.SendText(ch, data);
        return true;
    }

    private bool TrySendHexCommand(string cmd, string prefix, SerialChannel ch)
    {
        if (!cmd.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string data = cmd[prefix.Length..].Trim();
        if (data.Length == 0)
        {
            _monitor.Notify($"{prefix} 缺少数据,用法: {prefix} <十六进制>");
            return true;
        }
        _monitor.SendHex(ch, data);
        return true;
    }

    private void ShowHelp()
    {
        _monitor.Notify("可用命令: 10send<数据> / 11send<数据> 发送文本; 10sendhex / 11sendhex <hex> 发送十六进制帧; clear/cls 清空日志; exit/quit 退出; ESC 清空输入(空输入时退出)");
    }

    // ── 渲染 ──────────────────────────────────────────────────

    private void Render()
    {
        int w = Math.Clamp(Console.WindowWidth, 60, 160);
        int fixedLines = 12; // 除日志区外的固定行数
        int logLines = Math.Clamp(Console.WindowHeight - fixedLines, 3, 30);

        var sb = new StringBuilder();

        sb.AppendLine(Line('═', w));
        string title = " Modbus 串口测试仪表盘 ";
        sb.Append('║').Append(Fit(title.PadRight(w - 2), w - 2)).AppendLine("║");
        sb.AppendLine(Line('─', w));

        sb.AppendLine(Section("串口状态"));
        sb.AppendLine(StatusLine(_monitor.Host, w));
        sb.AppendLine(StatusLine(_monitor.Slave, w));
        sb.AppendLine(Line('─', w));

        sb.AppendLine(Section("收发统计"));
        sb.AppendLine(Fit($"{_monitor.Host.Name} 发送 {_monitor.Host.SentCount} 次 / {_monitor.Host.SentBytes} B   接收 {_monitor.Host.ReceivedCount} 次 / {_monitor.Host.ReceivedBytes} B", w));
        sb.AppendLine(Fit($"{_monitor.Slave.Name} 发送 {_monitor.Slave.SentCount} 次 / {_monitor.Slave.SentBytes} B   接收 {_monitor.Slave.ReceivedCount} 次 / {_monitor.Slave.ReceivedBytes} B", w));
        sb.AppendLine(Line('─', w));

        sb.AppendLine(Section($"实时日志(最多 {SerialMonitor.MaxLogs} 条)"));
        var logs = _monitor.GetLogs();
        int start = Math.Max(0, logs.Count - logLines);
        for (int i = start; i < logs.Count; i++)
            sb.AppendLine(LogLine(logs[i], w));
        for (int i = logs.Count; i < start + logLines; i++)
            sb.AppendLine(Fit("", w)); // 不足时补空行,避免旧内容残留

        sb.AppendLine(Line('─', w));
        sb.Append('>').Append(Fit(" " + _input + "█", w - 1));
        sb.AppendLine();
        sb.AppendLine(Line('═', w));

        Console.SetCursorPosition(0, 0);
        Console.Write(sb.ToString());
    }

    private static string StatusLine(SerialChannel ch, int w)
    {
        string state = ch.IsOpen ? "● 已连接" : "○ 未连接";
        string line = $"  {ch.Name,-5} {ch.Role}   状态: {state}   参数: {ch.Port.BaudRate},{ch.Port.DataBits},{ch.Port.Parity},{ch.Port.StopBits}";
        return Fit(line, w);
    }

    private static string LogLine(SerialMonitor.LogEntry e, int w)
    {
        string content = FormatContent(e.Content);
        string line = $"  [{e.Time:HH:mm:ss.fff}] {e.Source} {e.Direction} {(e.Direction.StartsWith("回显") ? "->" : "<-")} {content}";
        return Fit(line, w);
    }

    /// <summary>内容可打印则显示文本(转义换行/制表),否则以十六进制显示。</summary>
    private static string FormatContent(string s)
    {
        bool printable = s.All(c => !char.IsControl(c) || c is '\r' or '\n' or '\t');
        if (printable)
            return s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return string.Join(" ", s.Select(c => ((byte)c).ToString("X2")));
    }

    private static string Section(string text) => " " + text;

    private static string Line(char ch, int w) => new string(ch, w);

    /// <summary>截断或补齐到指定长度,防止长内容破坏布局。</summary>
    private static string Fit(string s, int width)
    {
        if (s.Length <= width) return s.PadRight(width);
        return s[..(width - 1)] + "…";
    }
}
