using System.Text;

namespace ModbusTest;

public static class Program
{
    public static void Main()
    {
        // 确保特殊字符(框线、●)能正常显示,失败则保持系统默认编码
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 忽略 */ }
        try { Console.Title = "Modbus 串口测试仪表盘"; } catch { /* 重定向下可能不可用 */ }

        var monitor = new SerialMonitor();
        monitor.Open();

        // 启动后自动进入可视化仪表盘
        var dashboard = new Dashboard(monitor);
        dashboard.Run();

        monitor.Close();
        try { Console.Clear(); } catch { /* 忽略 */ }
        Console.WriteLine("已退出,串口已关闭。");
    }
}
