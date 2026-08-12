using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace HeartRater;

/// <summary>
/// 非打包 (unpackaged) 应用的入口。
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static void Main(string[] args)
    {
        XamlCheckProcessRequirements();

        // 用 Windows App SDK 自托管部署引导（SelfContained 模式）
        global::Microsoft.UI.Xaml.Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App(args);
        });
    }
}
