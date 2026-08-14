namespace ModbusTest;

/// <summary>
/// 管理主机(COM10)与从机(COM11)两条串口通道,维护实时日志。
/// 从机收到数据后原样回显(模拟 Modbus 从机应答)。
/// </summary>
public class SerialMonitor
{
    public SerialChannel Host { get; }   // COM10,主机(主动发送)
    public SerialChannel Slave { get; }  // COM11,从机(被动接收并回显)

    public const int MaxLogs = 100;

    public record LogEntry(DateTime Time, string Source, string Direction, string Content);

    /// <summary>日志或统计发生变化时触发(可能来自后台线程)。</summary>
    public event Action? Changed;

    private readonly object _logLock = new();
    private readonly List<LogEntry> _logs = new();

    public SerialMonitor()
    {
        Host = new SerialChannel("COM10", "主机");
        Slave = new SerialChannel("COM11", "从机");

        Host.DataReceived += (ch, data) => AddLog("COM10", "接收", data);

        // 从机回显:将收到的数据原样发送回去
        Slave.DataReceived += (ch, data) =>
        {
            bool ok = Slave.Send(ToBytes(data));
            AddLog("COM11", ok ? "回显" : "回显失败", data);
        };
    }

    public void Open()
    {
        TryOpen(Host);
        TryOpen(Slave);
    }

    private void TryOpen(SerialChannel ch)
    {
        try
        {
            ch.Open();
            AddLog(ch.Name, "打开", "成功");
        }
        catch (Exception ex)
        {
            AddLog(ch.Name, "打开", $"失败: {ex.Message}");
        }
    }

    public void Close()
    {
        Host.Close();
        Slave.Close();
    }

    /// <summary>发送文本(UTF-8),成功返回 true。</summary>
    public bool SendText(SerialChannel ch, string text)
    {
        bool ok = !string.IsNullOrEmpty(text) && ch.SendText(text);
        AddLog(ch.Name, ok ? "发送" : "发送失败", text);
        return ok;
    }

    /// <summary>发送十六进制帧,成功返回 true;hex 非法时写入日志并返回 false。</summary>
    public bool SendHex(SerialChannel ch, string hex)
    {
        if (!SerialChannel.TryParseHex(hex, out byte[] bytes))
        {
            AddLog(ch.Name, "发送失败", $"非法十六进制: {hex}");
            return false;
        }
        bool ok = ch.Send(bytes);
        AddLog(ch.Name, ok ? "发送" : "发送失败", HexText(bytes));
        return ok;
    }

    public IReadOnlyList<LogEntry> GetLogs()
    {
        lock (_logLock) return _logs.ToArray();
    }

    public void ClearLogs()
    {
        lock (_logLock) _logs.Clear();
        Changed?.Invoke();
    }

    /// <summary>向日志写入一条系统提示(如未识别命令)。</summary>
    public void Notify(string message) => AddLog("系统", "提示", message);

    private void AddLog(string source, string direction, string content)
    {
        lock (_logLock)
        {
            _logs.Add(new LogEntry(DateTime.Now, source, direction, content));
            if (_logs.Count > MaxLogs) _logs.RemoveAt(0);
        }
        Changed?.Invoke();
    }

    private static byte[] ToBytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    private static string HexText(byte[] bytes) =>
        string.Join(" ", bytes.Select(b => b.ToString("X2")));
}
