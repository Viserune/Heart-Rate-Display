using System.Windows.Media;
using HeartRater.Services;

namespace HeartRater.ViewModels;

/// <summary>悬浮窗 ViewModel：心率数字与颜色绑定（样式/位置等窗口行为留在 HudWindow code-behind）。</summary>
public sealed class HudViewModel : ObservableObject
{
    private string _bpmText = "--";
    private Brush _bpmBrush = HrColors.PlaceholderBrush;

    public HudViewModel(BleHeartRateService ble)
    {
        ble.HeartRateReceived += OnHeartRateReceived;
        ble.Disconnected += () => SetBpm(-1);
    }

    public string BpmText
    {
        get => _bpmText;
        private set => SetProperty(ref _bpmText, value);
    }

    public Brush BpmBrush
    {
        get => _bpmBrush;
        private set => SetProperty(ref _bpmBrush, value);
    }

    private void OnHeartRateReceived(int bpm)
    {
        if (bpm > 0)
        {
            SetBpm(bpm);
        }
    }

    private void SetBpm(int bpm)
    {
        if (bpm <= 0)
        {
            BpmText = "--";
            BpmBrush = HrColors.PlaceholderBrush;
            return;
        }

        BpmText = bpm.ToString();
        BpmBrush = HrColors.GetBrush(bpm);
    }
}
