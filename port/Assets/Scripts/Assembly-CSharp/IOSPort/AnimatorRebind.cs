using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace IOSPort
{
    // Test aid ("animcheck" in a touch script): finds animators whose bindings are stale, by playing their states
    // before and after a Rebind() and comparing the poses (the lever and toll doors needed rebinding on iOS).
    public static class AnimatorRebind
    {
        // Rebinds an active animator, keeping its state, time and parameters. Returns false if it wasn't playing.
        public static bool Rebind(Animator animator)
        {
            if (!animator.isActiveAndEnabled || animator.runtimeAnimatorController == null || !animator.isInitialized)
                return false;
            var parameters = animator.parameters;
            var values = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                int id = parameters[i].nameHash;
                switch (parameters[i].type)
                {
                    case AnimatorControllerParameterType.Float: values[i] = animator.GetFloat(id); break;
                    case AnimatorControllerParameterType.Int: values[i] = animator.GetInteger(id); break;
                    default: values[i] = animator.GetBool(id); break;
                }
            }
            int layers = animator.layerCount;
            var states = new AnimatorStateInfo[layers];
            var weights = new float[layers];
            for (int l = 0; l < layers; l++)
            {
                // Mid-transition, carry on in the state it's heading to.
                states[l] = animator.IsInTransition(l) ? animator.GetNextAnimatorStateInfo(l) : animator.GetCurrentAnimatorStateInfo(l);
                weights[l] = animator.GetLayerWeight(l);
            }
            float speed = animator.speed;

            animator.Rebind();

            animator.speed = speed;
            for (int i = 0; i < parameters.Length; i++)
            {
                int id = parameters[i].nameHash;
                switch (parameters[i].type)
                {
                    case AnimatorControllerParameterType.Float: animator.SetFloat(id, (float)values[i]); break;
                    case AnimatorControllerParameterType.Int: animator.SetInteger(id, (int)values[i]); break;
                    case AnimatorControllerParameterType.Bool: animator.SetBool(id, (bool)values[i]); break;
                    default:
                        if ((bool)values[i])
                            animator.SetTrigger(id);
                        break;
                }
            }
            for (int l = 0; l < layers; l++)
            {
                if (l > 0)
                    animator.SetLayerWeight(l, weights[l]);
                if (states[l].fullPathHash != 0)
                    animator.Play(states[l].fullPathHash, l, states[l].normalizedTime);
            }
            // Show the pose now, without firing the events up to this point a second time.
            bool events = animator.fireEvents;
            animator.fireEvents = false;
            animator.Update(0f);
            animator.fireEvents = events;
            return true;
        }

        // Test aid ("animcheck" in a touch script): plays halfway through each of the active animators' animations
        // before and after a rebind and logs the ones whose result changes. It leaves the room's props in odd poses.
        public static void Check()
        {
            int total = 0, stale = 0, states = 0;
            foreach (var animator in Object.FindObjectsByType<Animator>(FindObjectsSortMode.None))
            {
                if (!animator.isActiveAndEnabled || animator.runtimeAnimatorController == null)
                    continue;
                total++;
                // States can't be listed at runtime, so try every state name the game's controllers use
                // (Documents/iosport-animstates.txt) and the clip names.
                if (stateNames == null)
                {
                    string path = Path.Combine(Application.persistentDataPath, "iosport-animstates.txt");
                    stateNames = File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
                }
                var clips = new List<string>();
                foreach (var name in stateNames)
                    if (name.Length > 0 && !clips.Contains(name) && animator.HasState(0, Animator.StringToHash(name)))
                        clips.Add(name);
                foreach (var clip in animator.runtimeAnimatorController.animationClips)
                    if (!clips.Contains(clip.name) && animator.HasState(0, Animator.StringToHash(clip.name)))
                        clips.Add(clip.name);
                states += clips.Count;
                animator.fireEvents = false;
                var before = new List<Dictionary<string, string>>();
                foreach (var clip in clips)
                    before.Add(Sample(animator, clip));
                animator.Rebind();
                var changed = new List<string>();
                for (int c = 0; c < clips.Count; c++)
                {
                    var after = Sample(animator, clips[c]);
                    foreach (var kv in after)
                        if (!before[c].TryGetValue(kv.Key, out string b) || b != kv.Value)
                            changed.Add(clips[c] + ":" + kv.Key + " [" + b + " -> " + kv.Value + "]");
                }
                animator.fireEvents = true;
                if (changed.Count == 0)
                    continue;
                stale++;
                Debug.Log("[IOSPort] animcheck stale '" + animator.name + "' in " + animator.gameObject.scene.name + ": " +
                          string.Join(", ", changed.GetRange(0, Mathf.Min(6, changed.Count))) + (changed.Count > 6 ? " +" + (changed.Count - 6) : ""));
            }
            Debug.Log("[IOSPort] animcheck " + stale + " of " + total + " animators were stale (" + states + " states played)");
        }

        static List<string> stateNames;

        static Dictionary<string, string> Sample(Animator animator, string state)
        {
            animator.Play(state, 0, 0.5f);
            animator.Update(0f);
            return Snapshot(animator.transform);
        }

        static Dictionary<string, string> Snapshot(Transform root)
        {
            var snap = new Dictionary<string, string>();
            void Add(Transform t, string path)
            {
                var r = t.GetComponent<Renderer>();
                var sr = r as SpriteRenderer;
                snap[path] = t.gameObject.activeSelf + " " + t.localPosition.ToString("F3") + t.localEulerAngles.ToString("F1") + t.localScale.ToString("F3") +
                             (r != null ? " " + r.enabled : "") + (sr != null ? " " + sr.color + (sr.sprite != null ? sr.sprite.name : "") : "");
                for (int i = 0; i < t.childCount; i++)
                    Add(t.GetChild(i), path + "/" + t.GetChild(i).name);
            }
            Add(root, root.name);
            return snap;
        }
    }
}
