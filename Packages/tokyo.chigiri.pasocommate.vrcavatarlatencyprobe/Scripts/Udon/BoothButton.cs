using UdonSharp;
using UnityEngine;

namespace PasocomMate.VRCAvatarLatencyProbe
{
    /// <summary>
    /// 計測開始ボタン。
    /// VRChat の Interact イベントを受け取り、TeleportLatencyProbe へ委譲する。
    /// _singleFallMode が true の場合、15×1 モードで開始する。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class BoothButton : UdonSharpBehaviour
    {
        [SerializeField] private TeleportLatencyProbe _probe;
        [SerializeField] private bool _singleFallMode = false;

        public override void Interact()
        {
            if (_singleFallMode)
                _probe.OnButtonPressSingleFall();
            else
                _probe.OnButtonPress();
        }
    }
}
