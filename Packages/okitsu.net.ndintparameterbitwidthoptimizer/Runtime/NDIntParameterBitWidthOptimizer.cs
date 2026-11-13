using System.Collections.Generic;
using UnityEngine;
using VRC.SDKBase;

namespace okitsu.net.ndintparameterbitwidthoptimizer.Runtime
{
    /// <summary>
    /// Enables Int expression parameter bit-width optimization on the generated avatar.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Oktnet/ND Int Parameter Bit Width Optimizer")]
    public sealed class NDIntParameterBitWidthOptimizer : MonoBehaviour, IEditorOnly
    {
        [Tooltip("パラメータ名を指定すると、そのパラメータはbit幅最適化の対象外になります。")]
        public List<string> excludedParameters = new List<string>();
    }
}
