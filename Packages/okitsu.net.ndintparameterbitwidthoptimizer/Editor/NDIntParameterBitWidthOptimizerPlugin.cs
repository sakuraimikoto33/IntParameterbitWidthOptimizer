using UnityEngine;
using nadena.dev.ndmf;

[assembly: ExportsPlugin(typeof(okitsu.net.ndparamcompressor.Editor.NDIntParameterBitWidthOptimizerPlugin))]

namespace okitsu.net.ndparamcompressor.Editor
{
    /// <summary>
    /// Runs after the generated controllers and menus are available.
    /// </summary>
    public sealed class NDIntParameterBitWidthOptimizerPlugin : Plugin<NDIntParameterBitWidthOptimizerPlugin>
    {
        public override string QualifiedName => "okitsu.net.ndintparameterbitwidthoptimizer";

        public override string DisplayName => "ND Int Parameter Bit Width Optimizer";

        public override Color? ThemeColor => new Color(0.2f, 0.65f, 0.9f);

        protected override void Configure()
        {
            InPhase(BuildPhase.Optimizing)
                .AfterPlugin("nadena.dev.modular-avatar")
                // VRCFury is currently invoked by the VRChat SDK callback rather than
                // registering an NDMF plugin. This dependency is harmless when absent;
                // the Optimizing phase itself is after VRCFury's callback in that case.
                .AfterPlugin("com.vrcfury.vrcfury")
                .Run("Optimize Int parameter bit widths", NDIntParameterBitWidthOptimizerPass.Execute);
        }
    }
}
