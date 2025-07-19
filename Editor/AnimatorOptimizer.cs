﻿#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using d4rkpl4y3r.AvatarOptimizer.Extensions;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using BlendableLayer = VRC.SDKBase.VRC_AnimatorLayerControl.BlendableLayer;

namespace d4rkpl4y3r.AvatarOptimizer
{
    // based on https://github.com/VRLabs/Avatars-3.0-Manager/blob/main/Editor/AnimatorCloner.cs
    public class AnimatorOptimizer
    {
        private string assetPath;
        private AnimatorController source;
        private AnimatorController target;
        private Dictionary<AnimatorState, AnimatorState> stateMap = new Dictionary<AnimatorState, AnimatorState>();
        private Dictionary<AnimatorStateMachine, AnimatorStateMachine> stateMachineMap = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
        private Dictionary<int, int> fxLayerMap = new Dictionary<int, int>();
        private HashSet<int> layersToMerge = new HashSet<int>();
        private HashSet<int> layersToDestroy = new HashSet<int>();
        private HashSet<string> boolsToChangeToFloat = new HashSet<string>();
        private HashSet<string> intsToChangeToFloat = new HashSet<string>();
        private List<(EditorCurveBinding binding, float value)> constantCurvesToAdd = new List<(EditorCurveBinding binding, float value)>();
        private bool isFxLayer = false;

        private AnimatorOptimizer(AnimatorController target, AnimatorController source)
        {
            this.target = target;
            this.source = source;
            assetPath = AssetDatabase.GetAssetPath(target);
        }

        public static AnimatorController Copy(AnimatorController source, string path, Dictionary<int, int> fxLayerMap)
        {
            if (AssetDatabase.IsSubAsset(source))
            {
                return Run(source, path, fxLayerMap, new List<int>(), new List<int>());
            }
            // I try to use CopyAsset for non FX layers as the other way broke falling animations with gogo loco
            // however I can't use it if the source is a sub asset
            AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(source), path);
            var target = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            target.name = $"{source.name}(Optimized)";
            var optimizer = new AnimatorOptimizer(target, source);
            optimizer.fxLayerMap = new Dictionary<int, int>(fxLayerMap);
            optimizer.FixAllLayerControlBehaviours();
            return target;
        }

        public static AnimatorController Run(AnimatorController source, string path, Dictionary<int, int> fxLayerMap, List<int> layersToMerge, List<int> layersToDestroy, List<(EditorCurveBinding binding, float value)> constantCurvesToAdd = null)
        {
            var target = new AnimatorController();
            target.name = $"{source.name}(Optimized)";
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<BinarySerializationSO>(), path);
            AssetDatabase.AddObjectToAsset(target, path);
            var optimizer = new AnimatorOptimizer(target, source);
            optimizer.fxLayerMap = new Dictionary<int, int>(fxLayerMap);
            optimizer.layersToMerge = new HashSet<int>(layersToMerge);
            optimizer.layersToDestroy = new HashSet<int>(layersToDestroy);
            optimizer.isFxLayer = constantCurvesToAdd != null;
            optimizer.constantCurvesToAdd = constantCurvesToAdd ?? new List<(EditorCurveBinding binding, float value)>();
            return optimizer.Run();
        }

        private AnimatorController Run() {
            var sourceLayers = source.layers;
            var sourceParameters = source.parameters;

            for (int i = 0; i < sourceLayers.Length; i++) {
                if (layersToMerge.Contains(i) && sourceLayers[i].stateMachine.states.Length >= 2) {
                    foreach (var condition in sourceLayers[i].stateMachine.EnumerateAllTransitions().SelectMany(x => x.conditions)) {
                        if (sourceParameters.Any(x => x.name == condition.parameter && x.type == AnimatorControllerParameterType.Int)) {
                            intsToChangeToFloat.Add(condition.parameter);
                        } else if (sourceParameters.Any(x => x.name == condition.parameter && x.type == AnimatorControllerParameterType.Bool)) {
                            boolsToChangeToFloat.Add(condition.parameter);
                        }
                    }
                }
            }

            var existingTargetParameters = new HashSet<string>(target.parameters.Select(x => x.name));
            foreach (var p in sourceParameters) {
                bool boolToFloat = boolsToChangeToFloat.Contains(p.name);
                bool intToFloat = intsToChangeToFloat.Contains(p.name);
                var newP = new AnimatorControllerParameter {
                    name = p.name,
                    type = (boolToFloat || intToFloat) ? AnimatorControllerParameterType.Float : p.type,
                    defaultBool = p.defaultBool,
                    defaultFloat = boolToFloat ? (p.defaultBool ? 1f : 0f) : intToFloat ? (float)p.defaultInt : p.defaultFloat,
                    defaultInt = p.defaultInt
                };
                if (existingTargetParameters.Add(newP.name)) {
                    target.AddParameter(newP);
                }
            }

            if (layersToMerge.Count > 0 || constantCurvesToAdd.Count > 0) {
                var blendTreeDummyWeight = new AnimatorControllerParameter {
                    name = "d4rkAvatarOptimizer_MergedLayers_Weight",
                    type = AnimatorControllerParameterType.Float,
                    defaultFloat = 1f,
                    defaultBool = true,
                    defaultInt = 1
                };
                target.AddParameter(blendTreeDummyWeight);
            }

            var syncedLayers = new List<(int layerIndex, AnimatorControllerLayer old)>();
            int layerIndex = 0;
            for (int i = 0; i < sourceLayers.Length; i++) {
                if (layersToMerge.Contains(i) || layersToDestroy.Contains(i)) {
                    continue;
                }
                AnimatorControllerLayer newL = CloneLayer(sourceLayers[i], i == 0);
                newL.name = target.MakeUniqueLayerName(newL.name);
                newL.stateMachine.name = newL.name;
                target.AddLayer(newL);
                if (newL.syncedLayerIndex >= 0)
                    syncedLayers.Add((layerIndex, sourceLayers[i]));
                layerIndex++;
            }

            if (syncedLayers.Count > 0)
            {
                var layers = target.layers;
                for (int i = 0; i < syncedLayers.Count; i++)
                {
                    var layer = layers[syncedLayers[i].layerIndex];
                    CloneOverrideMotions(layer, syncedLayers[i].old);
                }
                target.layers = layers;
            }

            MergeLayers();

            EditorUtility.SetDirty(target);

            return target;
        }

        private void FixAllLayerControlBehaviours()
        {
            var targetLayers = target.layers;
            for (int i = 0; i < targetLayers.Length; i++)
            {
                FixLayerControlBehavioursInStateMachine(targetLayers[i].stateMachine);
            }
            EditorUtility.SetDirty(target);
        }

        private void FixLayerControlBehavioursInStateMachine(AnimatorStateMachine stateMachine)
        {
            var behaviours = stateMachine.behaviours;
            for (int i = 0; i < behaviours.Length; i++)
            {
                FixLayerControlBehaviour(behaviours[i]);
            }
            var stateMachines = stateMachine.stateMachines;
            for (int i = 0; i < stateMachines.Length; i++)
            {
                FixLayerControlBehavioursInStateMachine(stateMachines[i].stateMachine);
            }
            var states = stateMachine.states;
            for (int i = 0; i < states.Length; i++)
            {
                behaviours = states[i].state.behaviours;
                for (int j = 0; j < behaviours.Length; j++)
                {
                    FixLayerControlBehaviour(behaviours[j]);
                }
            }
        }

        private void FixLayerControlBehaviour(StateMachineBehaviour behaviour)
        {
            if (behaviour is VRC_AnimatorLayerControl layerControl)
            {
                if (layerControl.playable == BlendableLayer.FX && fxLayerMap.TryGetValue(layerControl.layer, out int newLayer))
                {
                    layerControl.layer = newLayer;
                    EditorUtility.SetDirty(layerControl);
                }
            }
        }

        private AnimationClip CloneAndFlipCurves(AnimationClip clip)
        {
            var newClip = GameObject.Instantiate(clip);
            newClip.name = $"{clip.name}(Flipped)";
            newClip.ClearCurves();
            newClip.hideFlags = HideFlags.HideInHierarchy;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                curve = new AnimationCurve(curve.keys.Select(x => new Keyframe(x.time, 1 - x.value, -x.inTangent, -x.outTangent)).ToArray());
                AnimationUtility.SetEditorCurve(newClip, binding, curve);
            }
            AssetDatabase.AddObjectToAsset(newClip, assetPath);
            return newClip;
        }

        private AnimationClip CloneFromTime(AnimationClip clip, float time, string name = null)
        {
            var newClip = GameObject.Instantiate(clip);
            newClip.name = name ?? $"{clip.name}(From {time})";
            newClip.ClearCurves();
            newClip.hideFlags = HideFlags.HideInHierarchy;
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                AnimationUtility.SetEditorCurve(newClip, binding, new AnimationCurve(new Keyframe[] { new Keyframe(0, curve.Evaluate(time)) }));
            }
            AssetDatabase.AddObjectToAsset(newClip, assetPath);
            return newClip;
        }

        public static bool IsNullOrEmpty(Motion motion)
        {
            return motion == null || (motion is AnimationClip clip && clip.empty);
        }

        private void MergeLayers() {
            if (layersToMerge.Count == 0 && constantCurvesToAdd.Count == 0) {
                return;
            }
            var directBlendTree = new BlendTree() {
                hideFlags = HideFlags.HideInHierarchy,
                name = "MergedToggles",
                blendType = BlendTreeType.Direct
            };
            var motions = new List<ChildMotion>();
            BlendTree CreateBlendTree(string param, ChildMotion[] children, string name = null) {
                var tree = new BlendTree() {
                    hideFlags = HideFlags.HideInHierarchy,
                    name = name ?? param,
                    blendType = BlendTreeType.Simple1D,
                    useAutomaticThresholds = children.All(c => c.threshold == 0f),
                    maxThreshold = 1f,
                    minThreshold = 0f,
                    blendParameter = param,
                    children = children.Select(x => new ChildMotion() { motion = x.motion, timeScale = 1f, threshold = x.threshold}).ToArray()
                };
                AssetDatabase.AddObjectToAsset(tree, assetPath);
                return tree;
            }
            var motionTimeSampleCount = AvatarOptimizerSettings.MotionTimeApproximationSampleCount;
            var motionTimeSamplePoints = Enumerable.Range(0, motionTimeSampleCount).Select(x => (float)x / (motionTimeSampleCount - 1)).ToArray();
            Motion ConvertStateToMotion(AnimatorState s) {
                if (s.motion is BlendTree tree) {
                    return CloneBlendTree(null, tree);
                } else if (s.motion is AnimationClip clip) {
                    (AnimationCurve curve, bool isEulerAngleBinding)[] curves = AnimationUtility.GetCurveBindings(clip)
                        .Select(binding => (AnimationUtility.GetEditorCurve(clip, binding), binding.propertyName.Contains("localEulerAngles"))).ToArray();
                    float maxKeyframeTime = curves.Max(x => (float?)x.curve.keys.Max(y => y.time)) ?? 0;
                    if (!s.timeParameterActive || maxKeyframeTime == 0) {
                        return CloneFromTime(clip, 0, clip.name);
                    }
                    var interpolationPoints = motionTimeSamplePoints.ToList();
                    bool removedSomePoint = true;
                    while (removedSomePoint) {
                        removedSomePoint = false;
                        for (int i = 1; i < interpolationPoints.Count - 1; i++) {
                            var timeA = interpolationPoints[i - 1] * maxKeyframeTime;
                            var timeB = interpolationPoints[i + 1] * maxKeyframeTime;
                            var timeCandidate = interpolationPoints[i] * maxKeyframeTime;
                            bool allClose = true;
                            foreach (var curve in curves) {
                                var valueA = curve.curve.Evaluate(timeA);
                                var valueB = curve.curve.Evaluate(timeB);
                                var valueCandidate = curve.curve.Evaluate(timeCandidate);
                                var valueInterpolated = Mathf.Lerp(valueA, valueB, (timeCandidate - timeA) / (timeB - timeA));
                                var threshold = 0.01f * Mathf.Max(Mathf.Abs(valueA), Mathf.Abs(valueB), 1e-6f);
                                if ((Mathf.Abs(valueCandidate - valueInterpolated) > threshold)
                                    || (curve.isEulerAngleBinding && Mathf.Abs(valueA - valueB) > 90.5f)) {
                                    allClose = false;
                                    break;
                                }
                            }
                            if (allClose) {
                                interpolationPoints.RemoveAt(i);
                                removedSomePoint = true;
                            }
                        }
                    }
                    var childMotions = interpolationPoints.Select(x => new ChildMotion() {
                        motion = CloneFromTime(clip, x * maxKeyframeTime, $"{clip.name} ({Mathf.RoundToInt(x * 100)}%)"),
                        threshold = x }).ToArray();
                    return CreateBlendTree(s.timeParameter, childMotions);
                }
                return s.motion;
            }
            var sourceLayers = source.layers;
            foreach (var i in layersToMerge) {
                var layer = sourceLayers[i].stateMachine;
                var layerStates = layer.states;
                Motion layerMotion = null;
                if (layerStates.Length == 2) {
                    var layerMotions = layerStates.Select(x => ConvertStateToMotion(x.state)).ToArray();
                    if (IsNullOrEmpty(layerMotions[0]))
                        layerMotions[0] = CloneAndFlipCurves(layerMotions[1] as AnimationClip);
                    if (IsNullOrEmpty(layerMotions[1]))
                        layerMotions[1] = CloneAndFlipCurves(layerMotions[0] as AnimationClip);

                    var transitions = layer.anyStateTransitions.Concat(layerStates.SelectMany(x => x.state.transitions)).ToArray();
                    var singleIndex = transitions.Count(x => x.destinationState == layerStates[0].state) == 1 ? 1 : 0;
                    var andMotion = layerMotions[singleIndex];
                    var orMotion = layerMotions[1 - singleIndex];
                    foreach (var condition in transitions.First(c => c.destinationState == layerStates[singleIndex].state).conditions)
                    {
                        var innerTreeMotions = new ChildMotion[2] {
                            new ChildMotion() { motion = orMotion },
                            new ChildMotion() { motion = andMotion },
                        };
                        var param = source.parameters.FirstOrDefault(x => x.name == condition.parameter);
                        if (condition.mode == AnimatorConditionMode.IfNot)
                        {
                            innerTreeMotions = innerTreeMotions.Reverse().ToArray();
                        }
                        if (param.type == AnimatorControllerParameterType.Float)
                        {
                            innerTreeMotions[0].threshold = innerTreeMotions[1].threshold = condition.threshold;
                            if (condition.mode == AnimatorConditionMode.Less)
                            {
                                innerTreeMotions = innerTreeMotions.Reverse().ToArray();
                            }
                            innerTreeMotions[0].threshold -= 0.001f;
                            innerTreeMotions[1].threshold += 0.001f;
                        }
                        else if (param.type == AnimatorControllerParameterType.Int)
                        {
                            innerTreeMotions[0].threshold = innerTreeMotions[1].threshold = condition.threshold + 0.5f;
                            if (condition.mode == AnimatorConditionMode.Less)
                            {
                                innerTreeMotions[0].threshold = innerTreeMotions[1].threshold = condition.threshold - 0.5f;
                                innerTreeMotions = innerTreeMotions.Reverse().ToArray();
                            }
                            innerTreeMotions[0].threshold -= 0.25f;
                            innerTreeMotions[1].threshold += 0.25f;
                        }
                        andMotion = CreateBlendTree(condition.parameter, innerTreeMotions);
                    }
                    layerMotion = andMotion;
                } else if (layerStates.Length == 1) {
                    layerMotion = ConvertStateToMotion(layerStates[0].state);
                } else {
                    var transitions = layer.anyStateTransitions.OrderBy(x => x.conditions[0].threshold).ToArray();
                    var layerMotions = transitions.Select(x => ConvertStateToMotion(x.destinationState)).ToArray();
                    layerMotion = CreateBlendTree(transitions[0].conditions[0].parameter, layerMotions.Select((x, index) => new ChildMotion() { motion = x, threshold = index}).ToArray());
                }
                layerMotion.name = sourceLayers[i].name;
                motions.Add(new ChildMotion() {
                    motion = layerMotion,
                    directBlendParameter = "d4rkAvatarOptimizer_MergedLayers_Weight",
                    timeScale = 1f
                });
            }
            if (constantCurvesToAdd.Count > 0)
            {
                var layerMotion = new AnimationClip() {
                    hideFlags = HideFlags.None,
                    name = "d4rkAvatarOptimizer_MergedLayers_Constants",
                    frameRate = 60,
                    wrapMode = WrapMode.Default
                };
                foreach (var curve in constantCurvesToAdd)
                {
                    AnimationUtility.SetEditorCurve(layerMotion, curve.binding, AnimationCurve.Constant(0, 1f / 60f, curve.value));
                }
                AssetDatabase.AddObjectToAsset(layerMotion, assetPath);
                motions.Add(new ChildMotion() {
                    motion = layerMotion,
                    directBlendParameter = "d4rkAvatarOptimizer_MergedLayers_Weight",
                    timeScale = 1f
                });
            }
            directBlendTree.children = motions.ToArray();
            AssetDatabase.AddObjectToAsset(directBlendTree, assetPath);
            var state = new AnimatorState() {
                name = "d4rkAvatarOptimizer_MergedLayers",
                writeDefaultValues = true,
                hideFlags = HideFlags.HideInHierarchy,
                motion = directBlendTree
            };
            AssetDatabase.AddObjectToAsset(state, assetPath);
            var stateMachine = new AnimatorStateMachine() {
                name = "d4rkAvatarOptimizer_MergedLayers",
                hideFlags = HideFlags.HideInHierarchy,
                states = new ChildAnimatorState[1] {
                    new ChildAnimatorState() {
                        state = state,
                        position = new Vector3(250, 0, 0)
                    }
                }
            };
            AssetDatabase.AddObjectToAsset(stateMachine, assetPath);
            target.AddLayer(new AnimatorControllerLayer {
                avatarMask = null,
                blendingMode = AnimatorLayerBlendingMode.Override,
                defaultWeight = 1f,
                iKPass = false,
                name = target.MakeUniqueLayerName("d4rkAvatarOptimizer_MergedLayers"),
                syncedLayerAffectsTiming = false,
                stateMachine = stateMachine
            });
        }

        private AnimatorControllerLayer CloneLayer(AnimatorControllerLayer old, bool isFirstLayer = false)
        {
            var n = new AnimatorControllerLayer
            {
                avatarMask = old.avatarMask,
                blendingMode = old.blendingMode,
                defaultWeight = isFirstLayer ? 1f : old.defaultWeight,
                iKPass = old.iKPass,
                name = old.name,
                syncedLayerAffectsTiming = old.syncedLayerAffectsTiming,
                syncedLayerIndex = isFxLayer && fxLayerMap.TryGetValue(old.syncedLayerIndex, out int newLayerIndex) ? newLayerIndex : old.syncedLayerIndex,
                stateMachine = CloneStateMachine(old.stateMachine)
            };
            CloneTransitions(old.stateMachine, n.stateMachine);
            return n;
        }

        private void CloneOverrideMotions(AnimatorControllerLayer layer, AnimatorControllerLayer old)
        {
            foreach (var stateMotionPair in old.EnumerateAllMotionOverrides())
            {
                var state = stateMotionPair.state;
                var motion = stateMotionPair.motion;
                if (motion != null && stateMap.TryGetValue(state, out AnimatorState newState))
                {
                    layer.SetOverrideMotion(newState, motion);
                }
            }
        }

        private AnimatorStateMachine CloneStateMachine(AnimatorStateMachine old)
        {
            var n = new AnimatorStateMachine
            {
                anyStatePosition = old.anyStatePosition,
                entryPosition = old.entryPosition,
                exitPosition = old.exitPosition,
                hideFlags = old.hideFlags,
                name = old.name,
                parentStateMachinePosition = old.parentStateMachinePosition,
                stateMachines = old.stateMachines.Select(x => CloneChildStateMachine(x)).ToArray(),
                states = old.states.Select(x => CloneChildAnimatorState(x)).ToArray()
            };
            stateMachineMap[old] = n;
            
            AssetDatabase.AddObjectToAsset(n, assetPath);
            n.defaultState = FindMatchingState(old.defaultState);

            foreach (var oldb in old.behaviours)
            {
                var behaviour = n.AddStateMachineBehaviour(oldb.GetType());
                CloneBehaviourParameters(oldb, behaviour);
            }
            return n;
        }

        private ChildAnimatorStateMachine CloneChildStateMachine(ChildAnimatorStateMachine old)
        {
            var n = new ChildAnimatorStateMachine
            {
                position = old.position,
                stateMachine = CloneStateMachine(old.stateMachine)
            };
            return n;
        }

        private ChildAnimatorState CloneChildAnimatorState(ChildAnimatorState old)
        {
            var n = new ChildAnimatorState
            {
                position = old.position,
                state = CloneAnimatorState(old.state)
            };
            foreach (var oldb in old.state.behaviours)
            {
                var behaviour = n.state.AddStateMachineBehaviour(oldb.GetType());
                CloneBehaviourParameters(oldb, behaviour);
            }
            return n;
        }

        private AnimatorState CloneAnimatorState(AnimatorState old)
        {
            // Checks if the motion is a blend tree, to avoid accidental blend tree sharing between animator assets
            Motion motion = old.motion;
            if (motion is BlendTree oldTree)
            {
                var tree = CloneBlendTree(null, oldTree);
                motion = tree;
                // need to save the blend tree into the animator
                tree.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(motion, assetPath);
            }

            var n = new AnimatorState
            {
                cycleOffset = old.cycleOffset,
                cycleOffsetParameter = old.cycleOffsetParameter,
                cycleOffsetParameterActive = old.cycleOffsetParameterActive,
                hideFlags = old.hideFlags,
                iKOnFeet = old.iKOnFeet,
                mirror = old.mirror,
                mirrorParameter = old.mirrorParameter,
                mirrorParameterActive = old.mirrorParameterActive,
                motion = motion,
                name = old.name,
                speed = old.speed,
                speedParameter = old.speedParameter,
                speedParameterActive = old.speedParameterActive,
                tag = old.tag,
                timeParameter = old.timeParameter,
                timeParameterActive = old.timeParameterActive,
                writeDefaultValues = old.writeDefaultValues
            };
            stateMap[old] = n;
            AssetDatabase.AddObjectToAsset(n, assetPath);
            return n;
        }

        // Taken from here: https://gist.github.com/phosphoer/93ca8dcbf925fc006e4e9f6b799c13b0
        private BlendTree CloneBlendTree(BlendTree parentTree, BlendTree oldTree)
        {
            // Create a child tree in the destination parent, this seems to be the only way to correctly 
            // add a child tree as opposed to AddChild(motion)
            BlendTree pastedTree = new BlendTree();
            pastedTree.name = oldTree.name;
            pastedTree.blendType = oldTree.blendType;
            pastedTree.blendParameter = oldTree.blendParameter;
            pastedTree.blendParameterY = oldTree.blendParameterY;
            pastedTree.minThreshold = oldTree.minThreshold;
            pastedTree.maxThreshold = oldTree.maxThreshold;
            pastedTree.useAutomaticThresholds = oldTree.useAutomaticThresholds;

            // Recursively duplicate the tree structure
            // Motions can be directly added as references while trees must be recursively to avoid accidental sharing
            var source = oldTree.children;
            var children = new ChildMotion[source.Length];
            for(int i = 0; i < children.Length; i++)
            {
                var child = source[i];

                var childMotion = new ChildMotion
                {
                    timeScale = child.timeScale,
                    position = child.position,
                    cycleOffset = child.cycleOffset,
                    mirror = child.mirror,
                    threshold = child.threshold,
                    directBlendParameter = child.directBlendParameter
                };

                if (child.motion is BlendTree tree)
                {
                    var childTree = CloneBlendTree(pastedTree, tree);
                    childMotion.motion = childTree;
                    // need to save the blend tree into the animator
                    childTree.hideFlags = HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(childTree, assetPath);
                }
                else
                {
                    childMotion.motion = child.motion;
                }
                
                children[i] = childMotion;
            }
            pastedTree.children = children;

            CopyNormalizedBlendValuesProperty(oldTree, pastedTree);

            return pastedTree;
        }

        public static void CopyNormalizedBlendValuesProperty(BlendTree source, BlendTree target)
        {
            if (source.blendType != BlendTreeType.Direct)
            {
                return;
            }
            using (var sourceSO = new SerializedObject(source))
            {
                using (var targetSO = new SerializedObject(target))
                {
                    // copy the Normalized Blend Values toggle via serialized object since no api exists for it
                    var sourceProperty = sourceSO.FindProperty("m_NormalizedBlendValues");
                    var targetProperty = targetSO.FindProperty("m_NormalizedBlendValues");
                    targetProperty.boolValue = sourceProperty.boolValue;
                    targetSO.ApplyModifiedProperties();
                }
            }
        }

        private void CloneBehaviourParameters(StateMachineBehaviour old, StateMachineBehaviour n)
        {
            if (old.GetType() != n.GetType())
            {
                throw new System.ArgumentException("2 state machine behaviours that should be of the same type are not.");
            }
            switch (n)
            {
                case VRCAnimatorLayerControl l:
                    {
                        var o = old as VRCAnimatorLayerControl;
                        l.ApplySettings = o.ApplySettings;
                        l.blendDuration = o.blendDuration;
                        l.debugString = o.debugString;
                        l.goalWeight = o.goalWeight;
                        l.layer = o.layer;
                        l.playable = o.playable;
                        if (l.playable == BlendableLayer.FX && fxLayerMap.TryGetValue(o.layer, out int newLayer))
                        {
                            l.layer = newLayer;
                        }
                        break;
                    }
                case VRCAnimatorLocomotionControl l:
                    {
                        var o = old as VRCAnimatorLocomotionControl;
                        l.ApplySettings = o.ApplySettings;
                        l.debugString = o.debugString;
                        l.disableLocomotion = o.disableLocomotion;
                        break;
                    }
                case VRCAnimatorTemporaryPoseSpace l:
                    {
                        var o = old as VRCAnimatorTemporaryPoseSpace;
                        l.ApplySettings = o.ApplySettings;
                        l.debugString = o.debugString;
                        l.delayTime = o.delayTime;
                        l.enterPoseSpace = o.enterPoseSpace;
                        l.fixedDelay = o.fixedDelay;
                        break;
                    }
                case VRCAnimatorTrackingControl l:
                    {
                        var o = old as VRCAnimatorTrackingControl;
                        l.ApplySettings = o.ApplySettings;
                        l.debugString = o.debugString;
                        l.trackingEyes = o.trackingEyes;
                        l.trackingHead = o.trackingHead;
                        l.trackingHip = o.trackingHip;
                        l.trackingLeftFingers = o.trackingLeftFingers;
                        l.trackingLeftFoot = o.trackingLeftFoot;
                        l.trackingLeftHand = o.trackingLeftHand;
                        l.trackingMouth = o.trackingMouth;
                        l.trackingRightFingers = o.trackingRightFingers;
                        l.trackingRightFoot = o.trackingRightFoot;
                        l.trackingRightHand = o.trackingRightHand;
                        break;
                    }
                case VRCAvatarParameterDriver l:
                    {
                        var d = old as VRCAvatarParameterDriver;
                        l.debugString = d.debugString;
                        l.localOnly = d.localOnly;
                        l.isLocalPlayer = d.isLocalPlayer;
                        l.initialized = d.initialized;
                        l.parameters = d.parameters.ConvertAll(p =>
                        {
                            return new VRC_AvatarParameterDriver.Parameter 
                            { 
                                name = p.name, 
                                value = p.value, 
                                chance = p.chance, 
                                valueMin = p.valueMin, 
                                valueMax = p.valueMax, 
                                type = p.type, 
                                source = p.source, 
                                convertRange = p.convertRange, 
                                destMax = p.destMax, 
                                destMin = p.destMin, 
                                destParam = p.destParam, 
                                sourceMax = p.sourceMax, 
                                sourceMin = p.sourceMin, 
                                sourceParam = p.sourceParam
                            };
                        });
                        break;
                    }
                case VRCPlayableLayerControl l:
                    {
                        var o = old as VRCPlayableLayerControl;
                        l.ApplySettings = o.ApplySettings;
                        l.blendDuration = o.blendDuration;
                        l.debugString = o.debugString;
                        l.goalWeight = o.goalWeight;
                        l.layer = o.layer;
                        l.outputParamHash = o.outputParamHash;
                        break;
                    }
                default:
                    {
                        EditorUtility.CopySerialized(old, n);
                        break;
                    }
            }
        }

        private List<AnimatorState> GetStatesRecursive(AnimatorStateMachine sm)
        {
            List<AnimatorState> childrenStates = sm.states.Select(x => x.state).ToList();
            foreach (var child in sm.stateMachines.Select(x => x.stateMachine))
                childrenStates.AddRange(GetStatesRecursive(child));

            return childrenStates;
        }

        private List<AnimatorStateMachine> GetStateMachinesRecursive(AnimatorStateMachine sm,
            IDictionary<AnimatorStateMachine, AnimatorStateMachine> newAnimatorsByChildren = null)
        {
            List<AnimatorStateMachine> childrenSm = sm.stateMachines.Select(x => x.stateMachine).ToList();

            List<AnimatorStateMachine> gcsm = new List<AnimatorStateMachine>();
            gcsm.Add(sm);
            foreach (var child in childrenSm)
            {
                newAnimatorsByChildren?.Add(child, sm);
                gcsm.AddRange(GetStateMachinesRecursive(child, newAnimatorsByChildren));
            }
            
            return gcsm;
        }

        private AnimatorState FindMatchingState(AnimatorState original)
        {
            return original == null ? null : stateMap.TryGetValue(original, out AnimatorState state) ? state : null;
        }
        
        private AnimatorStateMachine FindMatchingStateMachine(AnimatorStateMachine original)
        {
            return original == null ? null : stateMachineMap.TryGetValue(original, out AnimatorStateMachine stateMachine) ? stateMachine : null;
        }

        private void CloneTransitions(AnimatorStateMachine old, AnimatorStateMachine n)
        {
            List<AnimatorState> oldStates = GetStatesRecursive(old);
            List<AnimatorState> newStates = GetStatesRecursive(n);
            var newAnimatorsByChildren = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            var oldAnimatorsByChildren = new Dictionary<AnimatorStateMachine, AnimatorStateMachine>();
            List<AnimatorStateMachine> oldStateMachines = GetStateMachinesRecursive(old, oldAnimatorsByChildren);
            List<AnimatorStateMachine> newStateMachines = GetStateMachinesRecursive(n, newAnimatorsByChildren);
            // Generate state transitions
            for (int i = 0; i < oldStates.Count; i++)
            {
                foreach (var transition in oldStates[i].transitions)
                {
                    AnimatorStateTransition newTransition = null;
                    if (transition.isExit && transition.destinationState == null && transition.destinationStateMachine == null)
                    {
                        newTransition = newStates[i].AddExitTransition();
                    }
                    else if (transition.destinationState != null)
                    {
                        var dstState = FindMatchingState(transition.destinationState);
                        if (dstState != null)
                            newTransition = newStates[i].AddTransition(dstState);
                    }
                    else if (transition.destinationStateMachine != null)
                    {
                        var dstState = FindMatchingStateMachine(transition.destinationStateMachine);
                        if (dstState != null)
                            newTransition = newStates[i].AddTransition(dstState);
                    }

                    if (newTransition != null)
                        ApplyTransitionSettings(transition, newTransition);
                }
            }
            
            for (int i = 0; i < oldStateMachines.Count; i++)
            {
                if(oldAnimatorsByChildren.ContainsKey(oldStateMachines[i]) && newAnimatorsByChildren.ContainsKey(newStateMachines[i]))
                {
                    foreach (var transition in oldAnimatorsByChildren[oldStateMachines[i]].GetStateMachineTransitions(oldStateMachines[i]))
                    {
                        AnimatorTransition newTransition = null;
                        if (transition.isExit && transition.destinationState == null && transition.destinationStateMachine == null)
                        {
                            newTransition = newAnimatorsByChildren[newStateMachines[i]].AddStateMachineExitTransition(newStateMachines[i]);
                        }
                        else if (transition.destinationState != null)
                        {
                            var dstState = FindMatchingState(transition.destinationState);
                            if (dstState != null)
                                newTransition = newAnimatorsByChildren[newStateMachines[i]].AddStateMachineTransition(newStateMachines[i], dstState);
                        }
                        else if (transition.destinationStateMachine != null)
                        {
                            var dstState = FindMatchingStateMachine(transition.destinationStateMachine);
                            if (dstState != null)
                                newTransition = newAnimatorsByChildren[newStateMachines[i]].AddStateMachineTransition(newStateMachines[i], dstState);
                        }

                        if (newTransition != null)
                            ApplyTransitionSettings(transition, newTransition);
                    }
                }
                // Generate AnyState transitions
                GenerateStateMachineBaseTransitions(oldStateMachines[i], newStateMachines[i], oldStates, newStates, oldStateMachines, newStateMachines);
            }
        }

        private void GenerateStateMachineBaseTransitions(AnimatorStateMachine old, AnimatorStateMachine n, List<AnimatorState> oldStates,
            List<AnimatorState> newStates, List<AnimatorStateMachine> oldStateMachines, List<AnimatorStateMachine> newStateMachines)
        {
            foreach (var transition in old.anyStateTransitions)
            {
                AnimatorStateTransition newTransition = null;
                if (transition.destinationState != null)
                {
                    var dstState = FindMatchingState(transition.destinationState);
                    if (dstState != null)
                        newTransition = n.AddAnyStateTransition(dstState);
                }
                else if (transition.destinationStateMachine != null)
                {
                    var dstState = FindMatchingStateMachine(transition.destinationStateMachine);
                    if (dstState != null)
                        newTransition = n.AddAnyStateTransition(dstState);
                }

                if (newTransition != null)
                    ApplyTransitionSettings(transition, newTransition);
            }

            // Generate EntryState transitions
            foreach (var transition in old.entryTransitions)
            {
                AnimatorTransition newTransition = null;
                if (transition.destinationState != null)
                {
                    var dstState = FindMatchingState(transition.destinationState);
                    if (dstState != null)
                        newTransition = n.AddEntryTransition(dstState);
                }
                else if (transition.destinationStateMachine != null)
                {
                    var dstState = FindMatchingStateMachine(transition.destinationStateMachine);
                    if (dstState != null)
                        newTransition = n.AddEntryTransition(dstState);
                }

                if (newTransition != null)
                    ApplyTransitionSettings(transition, newTransition);
            }
        }

        private void AddCondition(AnimatorTransitionBase transition, AnimatorCondition condition) {
            if (boolsToChangeToFloat.Contains(condition.parameter)) {
                var mode = condition.mode == AnimatorConditionMode.If ? AnimatorConditionMode.Greater : AnimatorConditionMode.Less;
                transition.AddCondition(mode, 0.5f, condition.parameter);
            } else if (intsToChangeToFloat.Contains(condition.parameter)) {
                if (condition.mode == AnimatorConditionMode.Equals) {
                    transition.AddCondition(AnimatorConditionMode.Less, condition.threshold + 0.5f, condition.parameter);
                    transition.AddCondition(AnimatorConditionMode.Greater, condition.threshold - 0.5f, condition.parameter);
                } else {
                    transition.AddCondition(condition.mode, condition.threshold + (condition.mode == AnimatorConditionMode.Greater ? 0.5f : -0.5f), condition.parameter);
                }
            } else {
                transition.AddCondition(condition.mode, condition.threshold, condition.parameter);
            }
        }

        private void ApplyTransitionSettings(AnimatorStateTransition transition, AnimatorStateTransition newTransition)
        {
            newTransition.canTransitionToSelf = transition.canTransitionToSelf;
            newTransition.duration = transition.duration;
            newTransition.exitTime = transition.exitTime;
            newTransition.hasExitTime = transition.hasExitTime;
            newTransition.hasFixedDuration = transition.hasFixedDuration;
            newTransition.hideFlags = transition.hideFlags;
            newTransition.isExit = transition.isExit;
            newTransition.mute = transition.mute;
            newTransition.name = transition.name;
            newTransition.offset = transition.offset;
            newTransition.interruptionSource = transition.interruptionSource;
            newTransition.orderedInterruption = transition.orderedInterruption;
            newTransition.solo = transition.solo;
            foreach (var condition in transition.conditions)
                AddCondition(newTransition, condition);
            
        }

        private void ApplyTransitionSettings(AnimatorTransition transition, AnimatorTransition newTransition)
        {
            newTransition.hideFlags = transition.hideFlags;
            newTransition.isExit = transition.isExit;
            newTransition.mute = transition.mute;
            newTransition.name = transition.name;
            newTransition.solo = transition.solo;
            foreach (var condition in transition.conditions)
                AddCondition(newTransition, condition);
        }
    }
}
#endif