using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace RpgActorUnity
{
    [InitializeOnLoad]
    internal static class RpgActorEditModeTicker
    {
        private const double Interval = 1 / 30.0;
        private static double _nextTick;
        private static bool _wasTicking;

        static RpgActorEditModeTicker() => EditorApplication.update += Tick;

        private static void Tick()
        {
            if (Application.isPlaying || EditorApplication.timeSinceStartup < _nextTick) return;

            var ticking = false;
            foreach (var actor in RpgActor.Enabled)
                ticking |= actor.NeedsUpdate;

            if (ticking || _wasTicking)
            {
                _nextTick = EditorApplication.timeSinceStartup + Interval;
                EditorApplication.QueuePlayerLoopUpdate();
            }
            if (!ticking && _wasTicking)
                InternalEditorUtility.RepaintAllViews();
            _wasTicking = ticking;
        }
    }
}
