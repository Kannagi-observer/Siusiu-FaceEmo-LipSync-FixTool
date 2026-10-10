// MIT License
//
// Copyright (c) 2026 Kannagiobserver
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

// Makes SiuSiu's animator-driven lip sync coexist with FaceEmo.
//
// Tools > Siusiu > Setup FaceEmo LipSync
//  1. Adds the lip sync blend shapes to FaceEmo's "Excluded Blend Shapes" and "Mouth Morph" lists.
//  2. Copies the avatar's FX controller to Assets/Siusiu_FaceEmo, sets the "Facial" layer weight to 0
//     and inserts a layer that resets the lip sync blend shapes before the "Talk Control" layer.
//     The original controller is not modified.
//
// Tools > Siusiu > Strip LipSync Curves From Selected Clips
//  Removes zero-valued lip sync blend shape curves from the selected expression clips (modifies them in place).
//
// Checked with FaceEmo 1.7.0 / Modular Avatar 1.18.7 / VRCSDK 3.10.5.
// This is an unofficial tool. It contains no avatar or FaceEmo assets. Back up your project before use.
//
// FaceEmo's assemblies are not auto-referenced, so everything goes through SerializedObject.
namespace SiusiuFaceEmoLipSync
{
    public static class SiusiuFaceEmoLipSyncSetup
    {
        private const string Title = "Siusiu FaceEmo Setup";
        private const string LauncherTypeName = "Suzuryg.FaceEmo.Components.FaceEmoLauncherComponent";
        private const string MeshPath = "Body";
        private const string OutputFolderName = "Siusiu_FaceEmo";
        private const string OutputDir = "Assets/" + OutputFolderName;
        private const string FacialLayerName = "Facial";
        private const string TalkControlLayerName = "Talk Control";
        private const string ResetLayerName = "Talk Reset";
        private const int FxLayerType = 5; // VRCAvatarDescriptor.AnimLayerType.FX

        // Blend shapes actually moved by SiuSiu_Talk_*.anim
        private static readonly string[] Excluded =
        {
            "mouth_a", "mouth_i", "mouth_u", "mouth_e", "mouth_o", "mouth_mu",
            "mouth_a2", "mouth_i2", "mouth_u2", "mouth_e2", "mouth_o2", "mouth_nn2",
            "mouth_a3", "mouth_i3", "mouth_u3", "mouth_e3", "mouth_o3", "mouth_nn3",
            "mouth_a6", "mouth_i6", "mouth_u6", "mouth_e6", "mouth_o6", "mouth_nn6",
        };

        // Mouth blend shapes zeroed by SiuSiu_Talk_*.anim while speaking (everything the clips touch except the above)
        private static readonly string[] MouthMorphs =
        {
            "mouth_H", "mouth_a2_big", "mouth_a2_big2", "mouth_a3_big",
            "mouth_a3_big2", "mouth_a3_big3", "mouth_a4", "mouth_a4_big",
            "mouth_a4_big2", "mouth_a5", "mouth_angry", "mouth_angry2",
            "mouth_angry2_i", "mouth_angry3", "mouth_angry3_i", "mouth_awawa",
            "mouth_awawa_big", "mouth_corners_smile", "mouth_e4", "mouth_e5",
            "mouth_evilsmile", "mouth_gizagiza", "mouth_gizagiza2", "mouth_i4",
            "mouth_i5", "mouth_mogumogu_L", "mouth_mogumogu_R", "mouth_mu2",
            "mouth_mu3", "mouth_narrow", "mouth_nn", "mouth_nn4",
            "mouth_nn5", "mouth_o4", "mouth_o5", "mouth_sad",
            "mouth_sad2", "mouth_sad2_open", "mouth_sad3", "mouth_sad4",
            "mouth_sad4_i", "mouth_sad4_open", "mouth_sad5", "mouth_sad5_i",
            "mouth_sad6", "mouth_sad_open", "mouth_smile", "mouth_smile2",
            "mouth_smile3", "mouth_smile3_i", "mouth_smile4", "mouth_smile4_i",
            "mouth_smile5", "mouth_smile5_big", "mouth_smile5_i", "mouth_smile6",
            "mouth_smile6_i", "mouth_surprised", "mouth_surprised2", "mouth_surprised2_i",
            "mouth_surprised3", "mouth_surprised_i", "mouth_tongue", "mouth_tongue2",
            "mouth_tongue3", "mouth_u4", "mouth_u5", "mouth_v",
            "mouth_v2", "mouth_wa", "mouth_wa2", "mouth_wa3",
            "mouth_wide", "mouth_\u0245", "mouth_\u0245_open", "mouth_\u0245_open2",
            "mouth_\u03C9", "mouth_\u03C9_open", "mouth_\u03C9_open2", "mouth_\u25A1",
            "mouth_\u25A12", "mouth_\u25A13", "mouth_\u25A13_big", "mouth_\u25B3",
            "mouth_\u25BD", "mouth_\u25BD2",
        };

        [MenuItem("Tools/Siusiu/Setup FaceEmo LipSync")]
        private static void Setup()
        {
            var launchers = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
                .Where(x => x != null && x.GetType().FullName == LauncherTypeName && x.gameObject.scene.IsValid())
                .ToList();
            if (launchers.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "FaceEmo object was not found in the open scenes.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog(Title,
                "This will, for each SiuSiu avatar targeted by FaceEmo in the open scenes:\n" +
                "- add blend shapes to FaceEmo's excluded / mouth morph lists\n" +
                $"- copy the FX controller to {OutputDir} and assign the copy to the avatar\n" +
                $"- set the \"{FacialLayerName}\" layer weight to 0 and add a \"{ResetLayerName}\" layer in the copy\n\n" +
                "Continue?", "OK", "Cancel"))
            {
                return;
            }

            var report = new List<string>();
            foreach (var launcher in launchers)
            {
                var setting = new SerializedObject(launcher).FindProperty("AV3Setting")?.objectReferenceValue;
                if (setting == null)
                {
                    report.Add($"{launcher.name}: AV3Setting is missing, skipped.");
                    continue;
                }

                var so = new SerializedObject(setting);
                var excludedProperty = so.FindProperty("ExcludedBlendShapes");
                var mouthMorphsProperty = so.FindProperty("MouthMorphs");
                if (excludedProperty == null || mouthMorphsProperty == null)
                {
                    report.Add($"{launcher.name}: unsupported FaceEmo version, skipped.");
                    continue;
                }

                // Make sure the target is a SiuSiu avatar before touching anything
                var avatar = so.FindProperty("TargetAvatar")?.objectReferenceValue as MonoBehaviour;
                var mesh = avatar != null ? avatar.transform.Find(MeshPath)?.GetComponent<SkinnedMeshRenderer>() : null;
                if (mesh == null || mesh.sharedMesh == null)
                {
                    report.Add($"{launcher.name}: target avatar has no \"{MeshPath}\" mesh, skipped.");
                    continue;
                }
                var descriptor = new SerializedObject(avatar);
                var fxProperty = FindFxProperty(descriptor);
                var fx = fxProperty?.objectReferenceValue as AnimatorController;
                if (fx == null || fx.layers.All(x => x.name != TalkControlLayerName) ||
                    Excluded.All(x => mesh.sharedMesh.GetBlendShapeIndex(x) < 0))
                {
                    report.Add($"{launcher.name} ({avatar.name}): not a SiuSiu avatar (no \"{TalkControlLayerName}\" layer or lip sync blend shapes), skipped.");
                    continue;
                }

                // FaceEmo settings
                var missing = Excluded.Concat(MouthMorphs).Where(x => mesh.sharedMesh.GetBlendShapeIndex(x) < 0).ToList();
                var excludedAdded = AddBlendShapes(excludedProperty, Excluded, mesh.sharedMesh);
                var mouthMorphsAdded = AddBlendShapes(mouthMorphsProperty, MouthMorphs, mesh.sharedMesh);
                // Lip sync blend shapes must not be cancelled while speaking
                var mouthMorphsRemoved = RemoveBlendShapes(mouthMorphsProperty, Excluded);
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(setting);
                EditorSceneManager.MarkSceneDirty(launcher.gameObject.scene);

                report.Add($"{launcher.name} ({avatar.name}): excluded +{excludedAdded}, mouth morph +{mouthMorphsAdded} / -{mouthMorphsRemoved}" +
                    (missing.Count > 0 ? $"\n  not found in mesh: {string.Join(", ", missing)}" : string.Empty));

                // FX controller
                report.Add("  FX: " + SetupFx(avatar, descriptor, fxProperty, mesh.sharedMesh));
            }

            AssetDatabase.SaveAssets();

            var message = string.Join("\n", report);
            Debug.Log($"[{Title}]\n" + message);
            EditorUtility.DisplayDialog(Title, message, "OK");
        }

        private static SerializedProperty FindFxProperty(SerializedObject descriptor)
        {
            var layers = descriptor.FindProperty("baseAnimationLayers");
            for (int i = 0; layers != null && layers.isArray && i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("type")?.intValue == FxLayerType)
                {
                    return element.FindPropertyRelative("animatorController");
                }
            }
            return null;
        }

        private static int AddBlendShapes(SerializedProperty list, IEnumerable<string> names, Mesh mesh)
        {
            var existing = new HashSet<(string, string)>();
            for (int i = 0; i < list.arraySize; i++)
            {
                var element = list.GetArrayElementAtIndex(i);
                existing.Add((element.FindPropertyRelative("_path").stringValue, element.FindPropertyRelative("_name").stringValue));
            }

            var added = 0;
            foreach (var name in names)
            {
                if (mesh.GetBlendShapeIndex(name) < 0 || !existing.Add((MeshPath, name))) { continue; }

                list.arraySize++;
                var element = list.GetArrayElementAtIndex(list.arraySize - 1);
                element.FindPropertyRelative("_path").stringValue = MeshPath;
                element.FindPropertyRelative("_name").stringValue = name;
                added++;
            }
            return added;
        }

        private static int RemoveBlendShapes(SerializedProperty list, IEnumerable<string> names)
        {
            var targets = new HashSet<string>(names);
            var removed = 0;
            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                var element = list.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("_path").stringValue == MeshPath &&
                    targets.Contains(element.FindPropertyRelative("_name").stringValue))
                {
                    list.DeleteArrayElementAtIndex(i);
                    removed++;
                }
            }
            return removed;
        }

        private static string SetupFx(MonoBehaviour avatar, SerializedObject descriptor, SerializedProperty fxProperty, Mesh mesh)
        {
            var fx = fxProperty.objectReferenceValue as AnimatorController;

            // Work on a copy so that the original asset stays untouched
            var notes = new List<string>();
            var fxPath = AssetDatabase.GetAssetPath(fx);
            if (!fxPath.StartsWith(OutputDir + "/"))
            {
                if (!AssetDatabase.IsValidFolder(OutputDir)) { AssetDatabase.CreateFolder("Assets", OutputFolderName); }
                var copyPath = AssetDatabase.GenerateUniqueAssetPath($"{OutputDir}/{fx.name}_FaceEmo.controller");
                if (!AssetDatabase.CopyAsset(fxPath, copyPath)) { return $"failed to copy {fxPath}, skipped."; }
                fx = AssetDatabase.LoadAssetAtPath<AnimatorController>(copyPath);
                fxProperty.objectReferenceValue = fx;
                descriptor.ApplyModifiedProperties();
                EditorSceneManager.MarkSceneDirty(avatar.gameObject.scene);
                notes.Add($"copied to {copyPath}");
            }

            var fxLayers = fx.layers.ToList();

            // Stop the original expression layer from writing blend shapes (its parameter drivers keep working)
            var facial = fxLayers.FirstOrDefault(x => x.name == FacialLayerName);
            if (facial == null)
            {
                notes.Add($"\"{FacialLayerName}\" layer not found");
            }
            else if (facial.defaultWeight != 0)
            {
                facial.defaultWeight = 0;
                notes.Add($"\"{FacialLayerName}\" weight -> 0");
            }

            // Insert a layer which resets the lip sync blend shapes
            if (fxLayers.All(x => x.name != ResetLayerName))
            {
                var clip = new AnimationClip { name = "SiuSiu_Talk_Reset" };
                foreach (var name in Excluded.Where(x => mesh.GetBlendShapeIndex(x) >= 0))
                {
                    clip.SetCurve(MeshPath, typeof(SkinnedMeshRenderer), "blendShape." + name, AnimationCurve.Constant(0, 1f / 60f, 0));
                }
                AssetDatabase.CreateAsset(clip, AssetDatabase.GenerateUniqueAssetPath($"{OutputDir}/{clip.name}.anim"));

                var stateMachine = new AnimatorStateMachine { name = ResetLayerName, hideFlags = HideFlags.HideInHierarchy };
                AssetDatabase.AddObjectToAsset(stateMachine, fx);
                var state = stateMachine.AddState("Reset");
                state.motion = clip;
                state.writeDefaultValues = false;

                var index = fxLayers.FindIndex(x => x.name == TalkControlLayerName);
                fxLayers.Insert(index, new AnimatorControllerLayer { name = ResetLayerName, stateMachine = stateMachine, defaultWeight = 1 });
                notes.Add($"\"{ResetLayerName}\" layer inserted");
            }

            fx.layers = fxLayers.ToArray();
            EditorUtility.SetDirty(fx);

            return notes.Count > 0 ? string.Join(", ", notes) : "already set up";
        }

        // Removes the lip sync blend shape curves from the selected expression clips.
        // Curves with non-zero values are part of the expression, so they are kept and reported.
        [MenuItem("Tools/Siusiu/Strip LipSync Curves From Selected Clips")]
        private static void StripSelectedClips()
        {
            var clips = Selection.GetFiltered<AnimationClip>(SelectionMode.Assets | SelectionMode.DeepAssets)
                .Where(x => AssetDatabase.GetAssetPath(x).StartsWith("Assets/") && AssetDatabase.IsMainAsset(x))
                .OrderBy(x => x.name)
                .ToList();
            if (clips.Count == 0)
            {
                EditorUtility.DisplayDialog(Title, "Select animation clips (or folders) under Assets in the Project window.", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog(Title,
                $"Zero-valued lip sync blend shape curves will be removed from {clips.Count} selected clip(s).\n" +
                "The clips are modified in place. Duplicate them first if you want to keep the originals.\n\nContinue?",
                "OK", "Cancel"))
            {
                return;
            }

            var targets = new HashSet<string>(Excluded.Select(x => "blendShape." + x));
            var report = new List<string>();
            foreach (var clip in clips)
            {
                var curves = AnimationUtility.GetCurveBindings(clip)
                    .Where(x => x.path == MeshPath && targets.Contains(x.propertyName))
                    .Select(x => (binding: x, curve: AnimationUtility.GetEditorCurve(clip, x)))
                    .Where(x => x.curve != null)
                    .ToList();
                if (curves.Count == 0) { continue; }

                // A clip which moves these blend shapes is a lip sync clip, not an expression
                if (curves.Any(x => x.curve.keys.Select(key => key.value).Distinct().Count() > 1))
                {
                    report.Add($"{clip.name}: animates lip sync blend shapes, skipped.");
                    continue;
                }

                var zero = curves.Where(x => x.curve.keys.All(key => key.value == 0)).ToList();
                var kept = curves.Except(zero).Select(x => x.binding.propertyName.Replace("blendShape.", string.Empty)).ToList();
                if (zero.Count > 0)
                {
                    Undo.RecordObject(clip, "Strip LipSync Curves");
                    foreach (var item in zero) { AnimationUtility.SetEditorCurve(clip, item.binding, null); }
                    EditorUtility.SetDirty(clip);
                }
                report.Add($"{clip.name}: removed {zero.Count}" + (kept.Count > 0 ? $", kept (non-zero): {string.Join(", ", kept)}" : string.Empty));
            }
            AssetDatabase.SaveAssets();

            var message = report.Count > 0 ? string.Join("\n", report) : "No lip sync curves were found.";
            Debug.Log($"[{Title}]\n" + message);
            EditorUtility.DisplayDialog(Title, message, "OK");
        }
    }
}
