using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace ModbusTest;

/// <summary>
/// 单个串口通道:负责打开/关闭、文本与十六进制发送、接收回调,
/// 并维护收发统计(次数/字节数)。线程安全。
/// </summary>
public class SerialChannel
{
    public string Name { get; }   // 如 "COM10"
    public string Role { get; }   // 如 "主机" / "从机"
    public SerialPort Port { get; }

    public bool IsOpen => Port.IsOpen;
    public long SentCount { get; private set; }
    public long SentBytes { get; private set; }
    public long ReceivedCount { get; private set; }
    public long ReceivedBytes { get; private set; }

    /// <summary>收到数据时触发,参数为收到的字符串(每个字节映射为一个 char)。</summary>
    public event Action<SerialChannel, string>? DataReceived;

    private readonly object _sync = new();
    private readonly object _recvLock = new();
    private readonly object _writeLock = new();

    public SerialChannel(string name, string role, int baudRate = 9600)
    {
        Name = name;
        Role = role;
        Port = new SerialPort(name, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 500,
            WriteTimeout = 500,
        };
        Port.DataReceived += OnDataReceived;
    }

    public void Open() => Port.Open();

    public void Close()
    {
        try { if (Port.IsOpen) Port.Close(); }
        catch (Exception) { /* 关闭失败忽略 */ }
    }

    /// <summary>发送原始字节,成功返回 true。写操作加锁,防止与回显等并发写入竞态。</summary>
    public bool Send(byte[] data)
    {
        try
        {
            if (!Port.IsOpen) return false;
            lock (_writeLock)
            {
                Port.Write(data, 0, data.Length);
            }
            Console.Error.WriteLine($"[DBG-{Name}] send len={data.Length} hex={string.Join(" ", data.Select(b => b.ToString("X2")))}");
            lock (_sync)
            {
                SentCount++;
                SentBytes += data.Length;
            }
            return true;
        }
        catch (Exception)
        {
            return false; // 端口被关闭或写入失败
        }
    }

    /// <summary>以 UTF-8 编码发送文本。</summary>
    public bool SendText(string text) => Send(Encoding.UTF8.GetBytes(text));

    /// <summary>解析十六进制字符串(如 "01 03 00 00 00 01 84 0A")并发送。</summary>
    public bool SendHex(string hex)
    {
        return TryParseHex(hex, out byte[] bytes) && Send(bytes);
    }

    /// <summary>解析十六进制字符串,允许空格/换行分隔,需为偶数个十六进制字符。</summary>
    public static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var cleaned = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0) return false;

        var result = new byte[cleaned.Length / 2];
        for (int i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(cleaned.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out result[i]))
                return false;
        }
        bytes = result;
        return true;
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        // 串行化接收处理:.NET 可能并发触发多个 DataReceived 回调,
        // 并发 ReadExisting 存在读到重复数据的竞态,用锁排队处理。
        lock (_recvLock)
        {
            try
            {
                if (!Port.IsOpen) return;
                int before = Port.BytesToRead;
                string data = Port.ReadExisting();
                int after = Port.BytesToRead;
                if (data.Length == 0) return;
                Console.Error.WriteLine($"[DBG-{Name}] recv before={before} len={data.Length} after={after} hex={string.Join(" ", data.Select(c => ((byte)c).ToString("X2")))}");

                lock (_sync)
                {
                    ReceivedCount++;
                    ReceivedBytes += Encoding.UTF8.GetByteCount(data);
                }
                DataReceived?.Invoke(this, data);
            }
            catch (Exception)
            {
                // 读取失败(如端口被关闭)忽略
            }
        }
    }
}
