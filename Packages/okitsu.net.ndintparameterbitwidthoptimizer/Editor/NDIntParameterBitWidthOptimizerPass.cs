using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using nadena.dev.ndmf;
using okitsu.net.ndintparameterbitwidthoptimizer.Runtime;
using Object = UnityEngine.Object;

namespace okitsu.net.ndparamcompressor.Editor
{
    /// <summary>
    /// Shrinks 8-bit Int expression parameters based on the values which survive into
    /// the generated avatar. Values are remapped in a monotonic, dense range so that
    /// comparisons such as Greater/Less remain equivalent.
    /// </summary>
    internal static class NDIntParameterBitWidthOptimizerPass
    {
        private const string LogPrefix = "[ND Int Parameter Bit Width Optimizer]";
        private const float IntegerTolerance = 0.0001f;

        private sealed class Candidate
        {
            internal readonly string Name;
            internal readonly bool Signed;
            internal readonly bool CanOptimize;
            internal readonly HashSet<int> Values = new HashSet<int>();
            internal readonly HashSet<int> ControllerDefaults = new HashSet<int>();
            internal bool Used;
            internal bool UsedInAnimator;
            internal bool UsedInMenu;
            internal bool Unsupported;
            internal string UnsupportedReason;
            internal bool HasOutOfRangeValue;
            internal Remap Remap;

            internal Candidate(string name, bool signed, bool canOptimize)
            {
                Name = name;
                Signed = signed;
                CanOptimize = canOptimize;
            }

            internal void MarkUnsupported(string reason)
            {
                Unsupported = true;
                if (string.IsNullOrEmpty(UnsupportedReason))
                {
                    UnsupportedReason = reason;
                }
            }
        }

        private sealed class Remap
        {
            internal readonly bool Signed;
            internal readonly byte BitWidth;
            internal readonly Dictionary<int, int> Values;

            internal Remap(bool signed, byte bitWidth, Dictionary<int, int> values)
            {
                Signed = signed;
                BitWidth = bitWidth;
                Values = values;
            }

            internal bool TryMap(int value, out int mapped)
            {
                if (Values.TryGetValue(value, out mapped))
                {
                    return true;
                }

                // Signed candidates that only need a smaller width use the identity
                // mapping. No remap entry is needed for values that remain valid.
                if (Signed && IsInRange(value, BitWidth, true))
                {
                    mapped = value;
                    return true;
                }

                mapped = 0;
                return false;
            }
        }

        private sealed class CopyReference
        {
            internal readonly string Source;
            internal readonly string Destination;
            internal readonly bool ConvertsRange;

            internal CopyReference(string source, string destination, bool convertsRange)
            {
                Source = source;
                Destination = destination;
                ConvertsRange = convertsRange;
            }
        }

        internal static void Execute(BuildContext context)
        {
            var markers = context.AvatarRootObject
                .GetComponentsInChildren<NDIntParameterBitWidthOptimizer>(true);
            if (markers == null || markers.Length == 0)
            {
                return;
            }

            try
            {
                var excludedParameters = new HashSet<string>(StringComparer.Ordinal);
                foreach (var marker in markers)
                {
                    if (marker?.excludedParameters == null)
                    {
                        continue;
                    }

                    foreach (var parameterName in marker.excludedParameters)
                    {
                        if (!string.IsNullOrWhiteSpace(parameterName))
                        {
                            excludedParameters.Add(parameterName.Trim());
                        }
                    }
                }

                Optimize(context, excludedParameters);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                // The marker is editor-only and must not survive into the generated avatar.
                foreach (var marker in markers)
                {
                    if (marker != null)
                    {
                        Object.DestroyImmediate(marker);
                    }
                }
            }
        }

        private static void Optimize(BuildContext context, HashSet<string> excludedParameters)
        {
            var descriptor = context.AvatarRootObject.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null || descriptor.expressionParameters == null)
            {
                Debug.Log($"{LogPrefix} Expression Parameters not found, skipping");
                return;
            }

            var candidates = CreateCandidates(descriptor.expressionParameters, excludedParameters);
            if (!candidates.Values.Any(candidate => candidate.CanOptimize))
            {
                Debug.Log($"{LogPrefix} No 8-bit Int expression parameters found");
                return;
            }

            var controllers = GetAnimatorControllers(descriptor).ToList();
            var copyReferences = new List<CopyReference>();
            foreach (var controller in controllers)
            {
                ScanController(controller, candidates, copyReferences);
            }

            ScanMenu(descriptor.expressionsMenu, candidates);

            // Defaults are relevant only after a parameter is actually referenced by
            // an Animator, a driver, or an Expression Menu.
            foreach (var candidate in candidates.Values)
            {
                if (!candidate.Used)
                {
                    continue;
                }

                foreach (var value in candidate.ControllerDefaults)
                {
                    candidate.Values.Add(value);
                }

                var expressionParameter = descriptor.expressionParameters.parameters
                    .FirstOrDefault(parameter => parameter.name == candidate.Name);
                if (expressionParameter != null)
                {
                    if (!TryGetInteger(expressionParameter.defaultValue, out var defaultValue))
                    {
                        candidate.MarkUnsupported("Expression parameter default is not an integer");
                    }
                    else
                    {
                        candidate.Values.Add(defaultValue);
                    }
                }
            }

            ResolveCopyReferences(copyReferences, candidates);

            foreach (var candidate in candidates.Values)
            {
                if (candidate.Used && candidate.Values.Count == 0)
                {
                    candidate.MarkUnsupported("No usable source values were found");
                }
            }

            foreach (var candidate in candidates.Values)
            {
                if (!candidate.CanOptimize || candidate.Unsupported)
                {
                    continue;
                }

                if (!candidate.Used || candidate.Values.Count == 0)
                {
                    continue;
                }

                if (!TryCreateRemap(candidate, out var remap))
                {
                    continue;
                }

                candidate.Remap = remap;
            }

            ValidateCopyRemaps(copyReferences, candidates);

            var changedCandidates = new List<Candidate>();
            foreach (var candidate in candidates.Values)
            {
                if (!candidate.CanOptimize)
                {
                    continue;
                }

                if (candidate.Unsupported)
                {
                    Debug.LogWarning($"{LogPrefix} {candidate.Name} skipped: {candidate.UnsupportedReason}");
                    continue;
                }

                if (candidate.Remap != null)
                {
                    changedCandidates.Add(candidate);
                }
            }

            if (changedCandidates.Count == 0)
            {
                Debug.Log($"{LogPrefix} No useful bit width reduction found");
                return;
            }

            var changedByName = changedCandidates.ToDictionary(candidate => candidate.Name);
            var controllersToChange = new HashSet<AnimatorController>();
            foreach (var controller in controllers)
            {
                if (ControllerUsesAnyCandidate(controller, changedByName))
                {
                    controllersToChange.Add(controller);
                }
            }

            var controllerMap = CloneControllersIfNeeded(context, descriptor, controllersToChange);
            foreach (var controller in controllersToChange)
            {
                var mutableController = controllerMap.TryGetValue(controller, out var clone)
                    ? clone
                    : controller;
                RewriteController(mutableController, changedByName);
            }

            var mutableParameters = EnsureMutableExpressionParameters(context, descriptor);
            RewriteExpressionParameters(mutableParameters, changedByName);
            descriptor.expressionParameters = mutableParameters;

            if (descriptor.expressionsMenu != null && changedCandidates.Any(candidate => candidate.UsedInMenu))
            {
                var mutableMenu = CloneMenuTreeIfNeeded(context, descriptor.expressionsMenu,
                    new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>());
                RewriteMenu(mutableMenu, changedByName, new HashSet<VRCExpressionsMenu>());
                descriptor.expressionsMenu = mutableMenu;
            }

            foreach (var candidate in changedCandidates)
            {
                Debug.Log($"{LogPrefix} {candidate.Name}: 8bit -> {candidate.Remap.BitWidth}bit " +
                          $"({candidate.Values.Count} used values)");
            }
        }

        private static Dictionary<string, Candidate> CreateCandidates(
            VRCExpressionParameters parameters,
            HashSet<string> excludedParameters)
        {
            var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            if (parameters.parameters == null)
            {
                return candidates;
            }

            foreach (var parameter in parameters.parameters)
            {
                if (parameter.valueType != VRCExpressionParameters.ValueType.Int ||
                    string.IsNullOrEmpty(parameter.name) || candidates.ContainsKey(parameter.name))
                {
                    continue;
                }

                var manuallyExcluded = excludedParameters.Contains(parameter.name);
                var canOptimize = parameter.networkSynced && parameter.bitWidth == 8 && !manuallyExcluded;
                if (!canOptimize)
                {
                    var reason = manuallyExcluded
                        ? "manually excluded"
                        : parameter.networkSynced
                        ? $"Expression parameter has an explicit {parameter.bitWidth}-bit width"
                        : "Expression parameter is local-only";
                    Debug.Log($"{LogPrefix} {parameter.name} tracked as a Copy source only: {reason}");
                }

                candidates.Add(parameter.name,
                    new Candidate(parameter.name, parameter.flippedRepresentation, canOptimize));
            }

            return candidates;
        }

        private static IEnumerable<AnimatorController> GetAnimatorControllers(VRCAvatarDescriptor descriptor)
        {
            var controllers = new HashSet<AnimatorController>();
            foreach (var layer in descriptor.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
            {
                if (layer.animatorController is AnimatorController controller)
                {
                    controllers.Add(controller);
                }
            }

            foreach (var layer in descriptor.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
            {
                if (layer.animatorController is AnimatorController controller)
                {
                    controllers.Add(controller);
                }
            }

            return controllers;
        }

        private static void ScanController(
            AnimatorController controller,
            Dictionary<string, Candidate> candidates,
            List<CopyReference> copyReferences)
        {
            foreach (var parameter in controller.parameters ?? Array.Empty<AnimatorControllerParameter>())
            {
                if (parameter.type != AnimatorControllerParameterType.Int || string.IsNullOrEmpty(parameter.name))
                {
                    continue;
                }

                if (!candidates.TryGetValue(parameter.name, out var candidate))
                {
                    candidate = new Candidate(parameter.name, false, false);
                    candidates.Add(parameter.name, candidate);
                    Debug.Log($"{LogPrefix} {parameter.name} tracked as a local Animator Int Copy source");
                }

                candidate.ControllerDefaults.Add(parameter.defaultInt);
            }

            foreach (var layer in controller.layers ?? Array.Empty<AnimatorControllerLayer>())
            {
                ScanStateMachine(
                    layer.stateMachine,
                    candidates,
                    copyReferences,
                    new HashSet<AnimatorStateMachine>());
            }
        }

        private static void ScanStateMachine(
            AnimatorStateMachine stateMachine,
            Dictionary<string, Candidate> candidates,
            List<CopyReference> copyReferences,
            HashSet<AnimatorStateMachine> visited)
        {
            if (stateMachine == null || !visited.Add(stateMachine))
            {
                return;
            }

            foreach (var transition in stateMachine.anyStateTransitions ?? Array.Empty<AnimatorStateTransition>())
            {
                ScanConditions(transition.conditions, candidates);
            }

            foreach (var transition in stateMachine.entryTransitions ?? Array.Empty<AnimatorTransition>())
            {
                ScanConditions(transition.conditions, candidates);
            }

            foreach (var behaviour in stateMachine.behaviours ?? Array.Empty<StateMachineBehaviour>())
            {
                ScanDriver(behaviour as VRCAvatarParameterDriver, candidates, copyReferences);
            }

            foreach (var childState in stateMachine.states ?? Array.Empty<ChildAnimatorState>())
            {
                var state = childState.state;
                if (state == null)
                {
                    continue;
                }

                foreach (var transition in state.transitions ?? Array.Empty<AnimatorStateTransition>())
                {
                    ScanConditions(transition.conditions, candidates);
                }

                foreach (var behaviour in state.behaviours ?? Array.Empty<StateMachineBehaviour>())
                {
                    ScanDriver(behaviour as VRCAvatarParameterDriver, candidates, copyReferences);
                }

                ScanMotion(state.motion, candidates, new HashSet<BlendTree>());
            }

            foreach (var childMachine in stateMachine.stateMachines ?? Array.Empty<ChildAnimatorStateMachine>())
            {
                foreach (var transition in stateMachine.GetStateMachineTransitions(childMachine.stateMachine) ??
                                         Array.Empty<AnimatorTransition>())
                {
                    ScanConditions(transition.conditions, candidates);
                }

                ScanStateMachine(childMachine.stateMachine, candidates, copyReferences, visited);
            }
        }

        private static void ScanConditions(IEnumerable<AnimatorCondition> conditions, Dictionary<string, Candidate> candidates)
        {
            foreach (var condition in conditions ?? Array.Empty<AnimatorCondition>())
            {
                if (!candidates.TryGetValue(condition.parameter, out var candidate))
                {
                    continue;
                }

                candidate.Used = true;
                candidate.UsedInAnimator = true;
                if (!TryGetInteger(condition.threshold, out var value))
                {
                    candidate.MarkUnsupported("Animator condition threshold is not an integer");
                }
                else
                {
                    candidate.Values.Add(value);
                }
            }
        }

        private static void ScanMotion(Motion motion, Dictionary<string, Candidate> candidates, HashSet<BlendTree> visited)
        {
            if (!(motion is BlendTree tree) || !visited.Add(tree))
            {
                return;
            }

            ScanBlendParameter(tree.blendParameter, tree.children, candidates);
            ScanBlendParameter(tree.blendParameterY, tree.children, candidates);

            foreach (var child in tree.children ?? Array.Empty<ChildMotion>())
            {
                if (tree.blendType == BlendTreeType.Direct &&
                    candidates.TryGetValue(child.directBlendParameter, out var directCandidate))
                {
                    directCandidate.Used = true;
                    directCandidate.UsedInAnimator = true;
                    directCandidate.Values.Add(0);
                    directCandidate.Values.Add(1);
                }

                ScanMotion(child.motion, candidates, visited);
            }
        }

        private static void ScanBlendParameter(
            string parameterName,
            IEnumerable<ChildMotion> children,
            Dictionary<string, Candidate> candidates)
        {
            if (!candidates.TryGetValue(parameterName, out var candidate))
            {
                return;
            }

            candidate.Used = true;
            candidate.UsedInAnimator = true;
            foreach (var child in children ?? Array.Empty<ChildMotion>())
            {
                if (!TryGetInteger(child.threshold, out var value))
                {
                    candidate.MarkUnsupported("Blend Tree threshold is not an integer");
                }
                else
                {
                    candidate.Values.Add(value);
                }
            }
        }

        private static void ScanDriver(
            VRCAvatarParameterDriver driver,
            Dictionary<string, Candidate> candidates,
            List<CopyReference> copyReferences)
        {
            if (driver == null || driver.parameters == null)
            {
                return;
            }

            foreach (var parameter in driver.parameters)
            {
                if (parameter.type == VRC_AvatarParameterDriver.ChangeType.Copy)
                {
                    copyReferences.Add(new CopyReference(
                        parameter.source,
                        parameter.name,
                        parameter.convertRange));

                    if (candidates.TryGetValue(parameter.source, out var sourceCandidate))
                    {
                        sourceCandidate.Used = true;
                        sourceCandidate.UsedInAnimator = true;
                    }
                }

                if (!candidates.TryGetValue(parameter.name, out var candidate))
                {
                    continue;
                }

                switch (parameter.type)
                {
                    case VRC_AvatarParameterDriver.ChangeType.Set:
                        candidate.Used = true;
                        candidate.UsedInAnimator = true;
                        if (!TryGetInteger(parameter.value, out var setValue))
                        {
                            candidate.MarkUnsupported("Parameter Driver Set value is not an integer");
                        }
                        else
                        {
                            candidate.Values.Add(setValue);
                        }
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Random:
                        candidate.Used = true;
                        candidate.UsedInAnimator = true;
                        if (!TryGetInteger(parameter.valueMin, out var randomMin) ||
                            !TryGetInteger(parameter.valueMax, out var randomMax))
                        {
                            candidate.MarkUnsupported("Parameter Driver Random range is not an integer");
                        }
                        else if (randomMin > randomMax)
                        {
                            candidate.MarkUnsupported("Parameter Driver Random range is inverted");
                        }
                        else if (!AddIntegerRange(candidate, randomMin, randomMax))
                        {
                            candidate.MarkUnsupported("Parameter Driver Random range is outside the 8-bit parameter range");
                        }
                        break;
                    case VRC_AvatarParameterDriver.ChangeType.Copy:
                        candidate.Used = true;
                        candidate.UsedInAnimator = true;
                        break;
                    default:
                        candidate.MarkUnsupported($"Parameter Driver {parameter.type} cannot be remapped safely");
                        break;
                }
            }
        }

        private static bool AddIntegerRange(Candidate candidate, int min, int max)
        {
            if (min < -128 || max > 255)
            {
                candidate.HasOutOfRangeValue = true;
                candidate.Values.Add(min);
                candidate.Values.Add(max);
                return !candidate.CanOptimize;
            }

            if (candidate.CanOptimize &&
                (!IsInRange(min, 8, candidate.Signed) || !IsInRange(max, 8, candidate.Signed)))
            {
                return false;
            }

            for (var value = min; value <= max; value++)
            {
                candidate.Values.Add(value);
            }

            return true;
        }

        private static void ResolveCopyReferences(
            IEnumerable<CopyReference> copyReferences,
            Dictionary<string, Candidate> candidates)
        {
            var references = copyReferences.ToArray();
            for (var pass = 0; pass <= references.Length; pass++)
            {
                var changed = false;
                foreach (var copy in references)
                {
                    if (copy.ConvertsRange)
                    {
                        if (candidates.TryGetValue(copy.Destination, out var convertedDestination))
                        {
                            convertedDestination.MarkUnsupported(
                                "Parameter Driver Copy converts between different value ranges");
                        }

                        if (candidates.TryGetValue(copy.Source, out var convertedSource))
                        {
                            convertedSource.MarkUnsupported(
                                "Parameter Driver Copy converts a value range and cannot be remapped safely");
                        }

                        continue;
                    }

                    if (copy.Source == copy.Destination)
                    {
                        continue;
                    }

                    if (!candidates.TryGetValue(copy.Destination, out var destination))
                    {
                        if (candidates.TryGetValue(copy.Source, out var sourceWithUnknownDestination))
                        {
                            sourceWithUnknownDestination.MarkUnsupported(
                                "Parameter Driver Copy destination is not a synced Int expression parameter");
                        }

                        continue;
                    }

                    if (!candidates.TryGetValue(copy.Source, out var source))
                    {
                        destination.MarkUnsupported(
                            "Parameter Driver Copy source is not a synced Int expression parameter");
                        continue;
                    }

                    if (source.HasOutOfRangeValue)
                    {
                        destination.MarkUnsupported(
                            $"Parameter Driver Copy source '{source.Name}' can produce values outside the 8-bit range");
                        continue;
                    }

                    if (source.Unsupported)
                    {
                        destination.MarkUnsupported(
                            $"Parameter Driver Copy source '{source.Name}' cannot be remapped safely");
                        continue;
                    }

                    foreach (var value in source.Values)
                    {
                        changed |= destination.Values.Add(value);
                    }
                }

                if (!changed)
                {
                    break;
                }
            }
        }

        private static void ValidateCopyRemaps(
            IEnumerable<CopyReference> copyReferences,
            Dictionary<string, Candidate> candidates)
        {
            foreach (var copy in copyReferences)
            {
                if (copy.ConvertsRange || copy.Source == copy.Destination ||
                    !candidates.TryGetValue(copy.Destination, out var destination))
                {
                    continue;
                }

                if (!candidates.TryGetValue(copy.Source, out var source))
                {
                    destination.MarkUnsupported(
                        "Parameter Driver Copy source is not a synced Int expression parameter");
                    continue;
                }

                if (source.Unsupported || destination.Unsupported)
                {
                    source.MarkUnsupported(
                        $"Parameter Driver Copy between '{source.Name}' and '{destination.Name}' cannot be remapped safely");
                    destination.MarkUnsupported(
                        $"Parameter Driver Copy between '{source.Name}' and '{destination.Name}' cannot be remapped safely");
                    continue;
                }

                if (source.Remap != null && destination.Remap == null)
                {
                    source.MarkUnsupported(
                        $"Parameter Driver Copy destination '{destination.Name}' is not remapped with the source");
                    continue;
                }

                if (destination.Remap == null)
                {
                    continue;
                }

                foreach (var value in source.Values)
                {
                    var sourceMapped = value;
                    if (source.Remap != null && !source.Remap.TryMap(value, out sourceMapped))
                    {
                        destination.MarkUnsupported(
                            $"Parameter Driver Copy source '{source.Name}' contains an unmappable value");
                        break;
                    }

                    if (!destination.Remap.TryMap(value, out var destinationMapped) ||
                        destinationMapped != sourceMapped)
                    {
                        source.MarkUnsupported(
                            $"Parameter Driver Copy source '{source.Name}' uses a different value mapping");
                        destination.MarkUnsupported(
                            $"Parameter Driver Copy source '{source.Name}' uses a different value mapping");
                        break;
                    }
                }
            }
        }

        private static void ScanMenu(VRCExpressionsMenu root, Dictionary<string, Candidate> candidates)
        {
            foreach (var menu in EnumerateMenus(root))
            {
                foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
                {
                    if (control == null)
                    {
                        continue;
                    }

                    ScanMenuParameter(control.parameter, control.value, candidates);
                    foreach (var parameter in control.subParameters ?? Array.Empty<VRCExpressionsMenu.Control.Parameter>())
                    {
                        ScanMenuSubParameter(parameter, candidates);
                    }
                }
            }
        }

        private static void ScanMenuParameter(
            VRCExpressionsMenu.Control.Parameter parameter,
            float value,
            Dictionary<string, Candidate> candidates)
        {
            if (parameter == null || !candidates.TryGetValue(parameter.name, out var candidate))
            {
                return;
            }

            candidate.Used = true;
            candidate.UsedInMenu = true;
            if (!TryGetInteger(value, out var integerValue))
            {
                candidate.MarkUnsupported("Expression Menu value is not an integer");
            }
            else
            {
                candidate.Values.Add(integerValue);
            }
        }

        private static void ScanMenuSubParameter(
            VRCExpressionsMenu.Control.Parameter parameter,
            Dictionary<string, Candidate> candidates)
        {
            if (parameter == null || !candidates.TryGetValue(parameter.name, out var candidate))
            {
                return;
            }

            // Radial/puppet controls can produce a continuous range of values. A
            // fixed-value remap cannot preserve that behavior safely.
            candidate.Used = true;
            candidate.UsedInMenu = true;
            candidate.MarkUnsupported("Expression Menu sub-parameter can produce continuous values");
        }

        private static IEnumerable<VRCExpressionsMenu> EnumerateMenus(VRCExpressionsMenu root)
        {
            if (root == null)
            {
                yield break;
            }

            var pending = new Stack<VRCExpressionsMenu>();
            var visited = new HashSet<VRCExpressionsMenu>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var menu = pending.Pop();
                if (menu == null || !visited.Add(menu))
                {
                    continue;
                }

                yield return menu;
                foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
                {
                    if (control != null && control.subMenu != null)
                    {
                        pending.Push(control.subMenu);
                    }
                }
            }
        }

        private static bool TryCreateRemap(Candidate candidate, out Remap remap)
        {
            remap = null;
            if (candidate.Values.Any(value => !IsInRange(value, 8, candidate.Signed)))
            {
                return false;
            }

            if (candidate.Signed)
            {
                var min = candidate.Values.Min();
                var max = candidate.Values.Max();
                var bits = RequiredSignedBits(min, max);
                if (bits >= 8)
                {
                    return false;
                }

                remap = new Remap(true, (byte)bits, new Dictionary<int, int>());
                return true;
            }

            var sorted = candidate.Values.OrderBy(value => value).ToArray();
            var mapping = new Dictionary<int, int>();
            var preservedPrefix = -1;
            while (candidate.Values.Contains(preservedPrefix + 1))
            {
                preservedPrefix++;
            }

            foreach (var value in sorted)
            {
                if (value <= preservedPrefix)
                {
                    mapping[value] = value;
                }
            }

            var next = preservedPrefix + 1;
            foreach (var value in sorted)
            {
                if (value > preservedPrefix)
                {
                    mapping[value] = next++;
                }
            }

            var maxMapped = mapping.Values.Max();
            var unsignedBits = RequiredUnsignedBits(maxMapped);
            if (unsignedBits >= 8)
            {
                return false;
            }

            remap = new Remap(false, (byte)unsignedBits, mapping);
            return mapping.Any(pair => pair.Key != pair.Value) || unsignedBits < 8;
        }

        private static int RequiredUnsignedBits(int maxValue)
        {
            var bits = 1;
            while (maxValue >= (1 << bits) && bits < 8)
            {
                bits++;
            }

            return bits;
        }

        private static int RequiredSignedBits(int minValue, int maxValue)
        {
            for (var bits = 1; bits <= 8; bits++)
            {
                var min = -(1 << (bits - 1));
                var max = (1 << (bits - 1)) - 1;
                if (minValue >= min && maxValue <= max)
                {
                    return bits;
                }
            }

            return 8;
        }

        private static bool IsInRange(int value, int bits, bool signed)
        {
            if (signed)
            {
                return value >= -(1 << (bits - 1)) && value <= (1 << (bits - 1)) - 1;
            }

            return value >= 0 && value <= (1 << bits) - 1;
        }

        private static bool TryGetInteger(float value, out int integer)
        {
            integer = Mathf.RoundToInt(value);
            return Mathf.Abs(value - integer) <= IntegerTolerance;
        }

        private static bool ControllerUsesAnyCandidate(
            AnimatorController controller,
            Dictionary<string, Candidate> candidates)
        {
            return candidates.Values.Any(candidate => candidate.Used &&
                                                      ControllerContainsParameter(controller, candidate.Name));
        }

        private static bool ControllerContainsParameter(AnimatorController controller, string name)
        {
            return controller.parameters.Any(parameter => parameter.name == name &&
                                                          parameter.type == AnimatorControllerParameterType.Int);
        }

        private static Dictionary<AnimatorController, AnimatorController> CloneControllersIfNeeded(
            BuildContext context,
            VRCAvatarDescriptor descriptor,
            HashSet<AnimatorController> controllers)
        {
            var result = new Dictionary<AnimatorController, AnimatorController>();
            foreach (var controller in controllers)
            {
                if (context.IsTemporaryAsset(controller) || !EditorUtility.IsPersistent(controller))
                {
                    continue;
                }

                var clone = Object.Instantiate(controller);
                clone.name = controller.name + " (ND Int Bit Width Optimized)";
                context.AssetSaver.SaveAsset(clone);
                result[controller] = clone;
            }

            ReplaceControllerReferences(descriptor.baseAnimationLayers, result, layers => descriptor.baseAnimationLayers = layers);
            ReplaceControllerReferences(descriptor.specialAnimationLayers, result, layers => descriptor.specialAnimationLayers = layers);
            return result;
        }

        private static void ReplaceControllerReferences(
            VRCAvatarDescriptor.CustomAnimLayer[] source,
            Dictionary<AnimatorController, AnimatorController> replacements,
            Action<VRCAvatarDescriptor.CustomAnimLayer[]> assign)
        {
            if (source == null || replacements.Count == 0)
            {
                return;
            }

            var layers = source.ToArray();
            var changed = false;
            for (var i = 0; i < layers.Length; i++)
            {
                if (!(layers[i].animatorController is AnimatorController original) ||
                    !replacements.TryGetValue(original, out var replacement))
                {
                    continue;
                }

                layers[i].animatorController = replacement;
                changed = true;
            }

            if (changed)
            {
                assign(layers);
            }
        }

        private static VRCExpressionParameters EnsureMutableExpressionParameters(
            BuildContext context,
            VRCAvatarDescriptor descriptor)
        {
            if (context.IsTemporaryAsset(descriptor.expressionParameters) ||
                !EditorUtility.IsPersistent(descriptor.expressionParameters))
            {
                return descriptor.expressionParameters;
            }

            var clone = Object.Instantiate(descriptor.expressionParameters);
            clone.name = descriptor.expressionParameters.name + " (ND Int Bit Width Optimized)";
            context.AssetSaver.SaveAsset(clone);
            return clone;
        }

        private static VRCExpressionsMenu CloneMenuTreeIfNeeded(
            BuildContext context,
            VRCExpressionsMenu menu,
            Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> replacements)
        {
            if (menu == null)
            {
                return null;
            }

            if (replacements.TryGetValue(menu, out var existing))
            {
                return existing;
            }

            var clone = context.IsTemporaryAsset(menu) || !EditorUtility.IsPersistent(menu)
                ? menu
                : Object.Instantiate(menu);
            if (clone != menu)
            {
                clone.name = menu.name + " (ND Int Bit Width Optimized)";
                context.AssetSaver.SaveAsset(clone);
            }

            replacements[menu] = clone;
            foreach (var control in clone.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control != null && control.subMenu != null)
                {
                    control.subMenu = CloneMenuTreeIfNeeded(context, control.subMenu, replacements);
                }
            }

            return clone;
        }

        private static void RewriteController(
            AnimatorController controller,
            Dictionary<string, Candidate> candidates)
        {
            var parameters = controller.parameters ?? Array.Empty<AnimatorControllerParameter>();
            foreach (var parameter in parameters)
            {
                if (parameter.type == AnimatorControllerParameterType.Int &&
                    candidates.TryGetValue(parameter.name, out var candidate) &&
                    candidate.Remap != null &&
                    candidate.Remap.TryMap(parameter.defaultInt, out var mapped))
                {
                    parameter.defaultInt = mapped;
                }
            }

            foreach (var layer in controller.layers ?? Array.Empty<AnimatorControllerLayer>())
            {
                RewriteStateMachine(layer.stateMachine, candidates, new HashSet<AnimatorStateMachine>());
            }

            EditorUtility.SetDirty(controller);
        }

        private static void RewriteStateMachine(
            AnimatorStateMachine stateMachine,
            Dictionary<string, Candidate> candidates,
            HashSet<AnimatorStateMachine> visited)
        {
            if (stateMachine == null || !visited.Add(stateMachine))
            {
                return;
            }

            foreach (var transition in stateMachine.anyStateTransitions ?? Array.Empty<AnimatorStateTransition>())
            {
                RewriteConditions(transition.conditions, candidates, conditions => transition.conditions = conditions);
            }

            foreach (var transition in stateMachine.entryTransitions ?? Array.Empty<AnimatorTransition>())
            {
                RewriteConditions(transition.conditions, candidates, conditions => transition.conditions = conditions);
            }

            foreach (var behaviour in stateMachine.behaviours ?? Array.Empty<StateMachineBehaviour>())
            {
                RewriteDriver(behaviour as VRCAvatarParameterDriver, candidates);
            }

            foreach (var childState in stateMachine.states ?? Array.Empty<ChildAnimatorState>())
            {
                var state = childState.state;
                if (state == null)
                {
                    continue;
                }

                foreach (var transition in state.transitions ?? Array.Empty<AnimatorStateTransition>())
                {
                    RewriteConditions(transition.conditions, candidates, conditions => transition.conditions = conditions);
                }

                foreach (var behaviour in state.behaviours ?? Array.Empty<StateMachineBehaviour>())
                {
                    RewriteDriver(behaviour as VRCAvatarParameterDriver, candidates);
                }

                RewriteMotion(state.motion, candidates, new HashSet<BlendTree>());
            }

            foreach (var childMachine in stateMachine.stateMachines ?? Array.Empty<ChildAnimatorStateMachine>())
            {
                foreach (var transition in stateMachine.GetStateMachineTransitions(childMachine.stateMachine) ??
                                         Array.Empty<AnimatorTransition>())
                {
                    RewriteConditions(transition.conditions, candidates, conditions => transition.conditions = conditions);
                }

                RewriteStateMachine(childMachine.stateMachine, candidates, visited);
            }
        }

        private static void RewriteConditions(
            AnimatorCondition[] conditions,
            Dictionary<string, Candidate> candidates,
            Action<AnimatorCondition[]> assign)
        {
            if (conditions == null)
            {
                return;
            }

            var rewritten = conditions.ToArray();
            for (var i = 0; i < rewritten.Length; i++)
            {
                if (!candidates.TryGetValue(rewritten[i].parameter, out var candidate) ||
                    candidate.Remap == null ||
                    !TryGetInteger(rewritten[i].threshold, out var value) ||
                    !candidate.Remap.TryMap(value, out var mapped))
                {
                    continue;
                }

                rewritten[i].threshold = mapped;
            }

            assign(rewritten);
        }

        private static void RewriteMotion(
            Motion motion,
            Dictionary<string, Candidate> candidates,
            HashSet<BlendTree> visited)
        {
            if (!(motion is BlendTree tree) || !visited.Add(tree))
            {
                return;
            }

            var children = tree.children ?? Array.Empty<ChildMotion>();
            var rewrittenChildren = children.ToArray();
            RewriteBlendParameter(tree.blendParameter, rewrittenChildren, candidates);
            RewriteBlendParameter(tree.blendParameterY, rewrittenChildren, candidates);
            tree.children = rewrittenChildren;

            foreach (var child in rewrittenChildren)
            {
                RewriteMotion(child.motion, candidates, visited);
            }
        }

        private static void RewriteBlendParameter(
            string parameterName,
            ChildMotion[] children,
            Dictionary<string, Candidate> candidates)
        {
            if (!candidates.TryGetValue(parameterName, out var candidate) || candidate.Remap == null)
            {
                return;
            }

            for (var i = 0; i < children.Length; i++)
            {
                if (!TryGetInteger(children[i].threshold, out var value) ||
                    !candidate.Remap.TryMap(value, out var mapped))
                {
                    continue;
                }

                children[i].threshold = mapped;
            }
        }

        private static void RewriteDriver(
            VRCAvatarParameterDriver driver,
            Dictionary<string, Candidate> candidates)
        {
            if (driver == null || driver.parameters == null)
            {
                return;
            }

            for (var i = 0; i < driver.parameters.Count; i++)
            {
                var parameter = driver.parameters[i];
                if (!candidates.TryGetValue(parameter.name, out var candidate) || candidate.Remap == null)
                {
                    continue;
                }

                if (parameter.type == VRC_AvatarParameterDriver.ChangeType.Set &&
                    TryGetInteger(parameter.value, out var value) &&
                    candidate.Remap.TryMap(value, out var mapped))
                {
                    parameter.value = mapped;
                    driver.parameters[i] = parameter;
                }
                else if (parameter.type == VRC_AvatarParameterDriver.ChangeType.Random &&
                         TryGetInteger(parameter.valueMin, out var randomMin) &&
                         TryGetInteger(parameter.valueMax, out var randomMax) &&
                         candidate.Remap.TryMap(randomMin, out var mappedMin) &&
                         candidate.Remap.TryMap(randomMax, out var mappedMax) &&
                         mappedMin <= mappedMax)
                {
                    parameter.valueMin = mappedMin;
                    parameter.valueMax = mappedMax;
                    driver.parameters[i] = parameter;
                }
            }

            EditorUtility.SetDirty(driver);
        }

        private static void RewriteExpressionParameters(
            VRCExpressionParameters parameters,
            Dictionary<string, Candidate> candidates)
        {
            if (parameters.parameters == null)
            {
                return;
            }

            for (var i = 0; i < parameters.parameters.Length; i++)
            {
                var parameter = parameters.parameters[i];
                if (parameter.valueType != VRCExpressionParameters.ValueType.Int ||
                    parameter.bitWidth != 8 ||
                    !candidates.TryGetValue(parameter.name, out var candidate) ||
                    candidate.Remap == null)
                {
                    continue;
                }

                parameter.bitWidth = candidate.Remap.BitWidth;
                if (TryGetInteger(parameter.defaultValue, out var value) &&
                    candidate.Remap.TryMap(value, out var mapped))
                {
                    parameter.defaultValue = mapped;
                }

                parameters.parameters[i] = parameter;
            }

            EditorUtility.SetDirty(parameters);
        }

        private static void RewriteMenu(
            VRCExpressionsMenu menu,
            Dictionary<string, Candidate> candidates,
            HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || !visited.Add(menu))
            {
                return;
            }

            foreach (var control in menu.controls ?? new List<VRCExpressionsMenu.Control>())
            {
                if (control == null)
                {
                    continue;
                }

                RewriteMenuParameter(control, candidates);

                RewriteMenu(control.subMenu, candidates, visited);
            }

            EditorUtility.SetDirty(menu);
        }

        private static void RewriteMenuParameter(
            VRCExpressionsMenu.Control control,
            Dictionary<string, Candidate> candidates)
        {
            var parameter = control?.parameter;
            if (parameter == null || !candidates.TryGetValue(parameter.name, out var candidate) ||
                candidate.Remap == null || !TryGetInteger(control.value, out var integerValue) ||
                !candidate.Remap.TryMap(integerValue, out var mapped))
            {
                return;
            }

            control.value = mapped;
        }
    }
}
