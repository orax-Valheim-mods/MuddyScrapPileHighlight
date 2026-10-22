using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace MuddyScrapPileHighlight
{
    /// <summary>
    /// Draws a translucent unlit copy of every pile mesh, pushed out along its normals
    /// by a few centimetres. The shell is unlit so it stays readable in the pitch-dark
    /// sunken crypts where a plain colour tint of the pile itself would be invisible,
    /// and it keeps the depth test, so terrain and walls still hide what they should.
    ///
    /// Shells are parented under the pile's own renderers: mining a piece, an LOD switch
    /// or a zone unload destroys them with their owner, so only distance changes, the
    /// hotkey and a disabled source renderer need explicit bookkeeping here.
    /// </summary>
    internal class ShellHighlighter
    {
        /// <summary>
        /// Tried in order until one exists in the running game: all are unlit and
        /// alpha-blended except the last, which is the opaque safety net.
        /// </summary>
        private static readonly string[] ShaderCandidates =
        {
            "Sprites/Default",
            "UI/Unlit/Transparent",
            "Custom/Particle (Unlit)",
            "Legacy Shaders/Particles/Alpha Blended",
            "Unlit/Color",
        };

        /// <summary>
        /// The shell's render queue. It stays on Transparent so the depth written by opaque
        /// geometry - crypt stone, walls, terrain - goes on occluding it. A later (Overlay)
        /// queue was tried to draw over water and cost the shell that occlusion, so water is
        /// handled by hiding it (HideCryptWater) instead.
        /// </summary>
        private const int TransparentQueue = 3000;

        /// <summary>
        /// How often the source renderers are re-read to follow mining. The shell only has
        /// to catch a piece being mined away, which the game does over whole seconds, so a
        /// few reads per second is plenty - doing it every frame cost several native calls
        /// per shell for nothing.
        /// </summary>
        private const float RefreshInterval = 0.2f;

        /// <summary>One pile mesh and the shell drawn over it.</summary>
        private sealed class ShellBinding
        {
            internal Renderer Source;
            internal MeshFilter SourceFilter;
            internal MeshFilter ShellFilter;
            internal MeshRenderer Shell;

            /// <summary>The mesh the shell was grown from, to notice the game rewriting it.</summary>
            internal Mesh Mesh;

            /// <summary>Its vertex count, to notice a rewrite that kept the same instance.</summary>
            internal int VertexCount;

            internal bool Visible = true;
        }

        private readonly ModConfig config;
        private readonly ManualLogSource log;

        /// <summary>Tracked pile -> the shells created under it.</summary>
        private readonly Dictionary<GameObject, List<ShellBinding>> shells =
            new Dictionary<GameObject, List<ShellBinding>>();

        /// <summary>Expanded mesh per source mesh: all piles share the prefab meshes.</summary>
        private readonly Dictionary<Mesh, Mesh> expandedMeshes = new Dictionary<Mesh, Mesh>();

        private readonly List<GameObject> removeScratch = new List<GameObject>();

        private Material shellMaterial;
        private string shellTintProperty;
        private float appliedWidth = -1f;
        private Color appliedColor = new Color(0f, 0f, 0f, 0f);
        private float nextShaderRetry;
        private float nextStateLog;
        private bool loggedShaderError;
        private bool occluderProbed;
        private float nextWaterNet;
        private bool appliedHideCrypt;
        private float nextRefresh;

        /// <summary>Crypt water surfaces this mod hid, to show them again on restore.</summary>
        private readonly HashSet<Renderer> hiddenWater = new HashSet<Renderer>();

        internal ShellHighlighter(ModConfig config, ManualLogSource log)
        {
            this.config = config;
            this.log = log;
        }

        /// <summary>
        /// Brings the drawn shells in line with the tracked piles and the config.
        /// The per-frame path is a couple of config compares and one timer: pruning and
        /// shell creation only run when a scan actually changed the tracked set, and the
        /// source renderers are re-read a few times a second instead of every frame.
        /// </summary>
        /// <param name="changed">
        /// True when the scan tick just changed the tracked set, so shells have to be
        /// created for new piles and pruned from the ones that left.
        /// </param>
        internal void Sync(HashSet<GameObject> tracked, bool changed)
        {
            if (!config.Shell.Value || !config.Enabled.Value)
            {
                Clear();
                return;
            }

            if (!EnsureMaterial())
            {
                return;
            }

            SyncWater();

            float width = config.ShellWidth.Value;
            if (!Mathf.Approximately(width, appliedWidth))
            {
                // The width feeds the expanded meshes, so every shell is rebuilt.
                DestroyShells();
                ClearMeshCache();
                appliedWidth = width;
                changed = true;
            }

            Color color = config.ShellColor.Value;
            if (color != appliedColor)
            {
                shellMaterial.SetColor(shellTintProperty, color);
                appliedColor = color;
            }

            if (config.DebugLog.Value)
            {
                ProbeOccluder();
            }

            if (changed)
            {
                Prune(tracked);

                foreach (GameObject pile in tracked)
                {
                    if (pile == null || shells.ContainsKey(pile))
                    {
                        continue;
                    }

                    // A tracked parent already owns everything below it: dressing the child
                    // as well would draw the very same surface twice and flicker.
                    if (HasTrackedAncestor(pile, tracked))
                    {
                        continue;
                    }

                    CreateShells(pile);
                }
            }

            // Mining a piece only has to be picked up within a fraction of a second, so the
            // (several native calls per shell) re-read runs on a timer, not every frame.
            if (Time.time >= nextRefresh)
            {
                nextRefresh = Time.time + RefreshInterval;
                RefreshSources();
            }

            if (config.DebugLog.Value && Time.time >= nextStateLog)
            {
                // A compact state line per pile: if no transition ever fires while pieces
                // vanish, this still shows what the game does to them.
                nextStateLog = Time.time + 5f;
                LogBindingStates();
            }
        }

        private void LogBindingStates()
        {
            foreach (KeyValuePair<GameObject, List<ShellBinding>> pair in shells)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                int visibleCount = 0;
                ShellBinding sample = null;
                foreach (ShellBinding binding in pair.Value)
                {
                    if (binding.Shell != null && binding.Shell.enabled)
                    {
                        visibleCount++;
                    }

                    sample = sample ?? binding;
                }

                string state = "<no binding>";
                if (sample != null)
                {
                    Mesh mesh = sample.SourceFilter != null ? sample.SourceFilter.sharedMesh : null;
                    state =
                        sample.Source == null
                            ? "source destroyed"
                            : "active="
                                + sample.Source.gameObject.activeInHierarchy
                                + " enabled="
                                + sample.Source.enabled
                                + " forcedOff="
                                + sample.Source.forceRenderingOff
                                + " layer="
                                + sample.Source.gameObject.layer
                                + " shellActive="
                                + (
                                    sample.Shell != null
                                    && sample.Shell.gameObject.activeInHierarchy
                                )
                                + " mesh="
                                + (mesh != null ? $"{mesh.name} ({mesh.vertexCount}v)" : "<empty>");
                }

                log.LogInfo(
                    $"Shell state {pair.Key.name}: {visibleCount}/{pair.Value.Count} shells "
                        + $"visible; sample: {state}"
                );
            }
        }

        /// <summary>Removes every shell. Called on disable, shutdown and script reload.</summary>
        internal void Clear()
        {
            // Hidden crypt water is global game state, so it is put back even when there
            // was no shell left to destroy. Nothing stays hidden afterwards either, so the
            // next activation re-scans promptly instead of waiting out the slow timer.
            RestoreCryptWater();
            appliedHideCrypt = false;

            if (shells.Count == 0 && expandedMeshes.Count == 0 && appliedWidth < 0f)
            {
                return;
            }

            DestroyShells();
            ClearMeshCache();
            appliedWidth = -1f;
        }

        /// <summary>
        /// Drops the entries whose pile was mined or unloaded - the shells themselves are
        /// already gone, they lived under the pile - the entries that left the tracked set
        /// (out of range, filter edited, mod disabled) and the entries a tracked parent has
        /// taken over. A destroyed key compares equal to any other destroyed key in Unity,
        /// so removal runs through the captured keys only.
        /// </summary>
        private void Prune(HashSet<GameObject> tracked)
        {
            if (shells.Count == 0)
            {
                return;
            }

            removeScratch.Clear();
            foreach (GameObject pile in shells.Keys)
            {
                if (pile == null)
                {
                    // The pile is gone and took its shell children with it.
                    removeScratch.Add(pile);
                    continue;
                }

                if (!tracked.Contains(pile) || HasTrackedAncestor(pile, tracked))
                {
                    DestroyShellsOf(pile);
                    removeScratch.Add(pile);
                }
            }

            foreach (GameObject pile in removeScratch)
            {
                shells.Remove(pile);
            }
        }

        private static bool HasTrackedAncestor(GameObject pile, HashSet<GameObject> tracked)
        {
            Transform parent = pile.transform.parent;
            while (parent != null)
            {
                if (tracked.Contains(parent.gameObject))
                {
                    return true;
                }

                parent = parent.parent;
            }

            return false;
        }

        private void DestroyShellsOf(GameObject pile)
        {
            if (!shells.TryGetValue(pile, out List<ShellBinding> bindings))
            {
                return;
            }

            foreach (ShellBinding binding in bindings)
            {
                if (binding.Shell != null)
                {
                    Object.Destroy(binding.Shell.gameObject);
                }
            }
        }

        /// <summary>
        /// MineRock5 hides mined pieces without destroying them, and the way it takes -
        /// disabling the renderer, forcing it off, emptying or rewriting the mesh, even
        /// changing the layer so the piece falls out of a culled layer - differs from piece
        /// to piece. The shell follows every one of them, so the orange shrinks with the
        /// pile instead of outliving it. A deactivated piece hides its shell without any
        /// help: the shell is a child of it.
        /// </summary>
        private void RefreshSources()
        {
            foreach (List<ShellBinding> bindings in shells.Values)
            {
                foreach (ShellBinding binding in bindings)
                {
                    if (binding.Shell == null)
                    {
                        continue;
                    }

                    Mesh current =
                        binding.SourceFilter != null ? binding.SourceFilter.sharedMesh : null;

                    if (current != binding.Mesh)
                    {
                        // The game swapped the mesh as pieces were mined: grow a new shell
                        // from it, or drop to nothing when it emptied it.
                        binding.Mesh = current;
                        binding.VertexCount = current != null ? current.vertexCount : 0;
                        binding.ShellFilter.sharedMesh =
                            current == null ? null : ResolveShellMesh(current);
                    }
                    else if (current != null && current.vertexCount != binding.VertexCount)
                    {
                        // Or rewrote the mesh in place, keeping the very same instance:
                        // the cached expansion is stale and has to be grown again.
                        int previous = binding.VertexCount;
                        binding.VertexCount = current.vertexCount;
                        DropCachedMesh(current);
                        binding.ShellFilter.sharedMesh = ResolveShellMesh(current);

                        if (config.DebugLog.Value)
                        {
                            log.LogInfo(
                                $"Shell source {DescribeSource(binding)}: mesh rewritten in "
                                    + $" place ({previous} -> {binding.VertexCount} vertices)"
                            );
                        }
                    }

                    if (binding.Source != null)
                    {
                        // Whatever layer hides the piece must hide the shell with it.
                        binding.Shell.gameObject.layer = binding.Source.gameObject.layer;
                    }

                    bool visible =
                        binding.Source != null
                        && binding.Source.enabled
                        && !binding.Source.forceRenderingOff
                        && current != null;
                    if (visible == binding.Visible)
                    {
                        continue;
                    }

                    binding.Visible = visible;
                    binding.Shell.enabled = visible;

                    if (config.DebugLog.Value)
                    {
                        log.LogInfo(
                            $"Shell source {DescribeSource(binding)}: "
                                + (visible ? "shown again" : $"hidden ({HideReason(binding)})")
                        );
                    }
                }
            }
        }

        /// <summary>Forgets the expansion of a mesh the game rewrote in place.</summary>
        private void DropCachedMesh(Mesh source)
        {
            if (
                expandedMeshes.TryGetValue(source, out Mesh expanded)
                && expanded != null
                && expanded != source
            )
            {
                // Never the source itself: an unreadable mesh is cached as itself.
                Object.Destroy(expanded);
            }

            expandedMeshes.Remove(source);
        }

        private static string DescribeSource(ShellBinding binding)
        {
            if (binding.Source != null)
            {
                return binding.Source.name;
            }

            return binding.SourceFilter != null ? binding.SourceFilter.name : "<destroyed>";
        }

        private static string HideReason(ShellBinding binding)
        {
            if (binding.Source == null)
            {
                return "source destroyed";
            }

            if (!binding.Source.enabled)
            {
                return "renderer disabled";
            }

            return binding.Source.forceRenderingOff ? "renderer forced off" : "mesh emptied";
        }

        private void DestroyShells()
        {
            foreach (List<ShellBinding> bindings in shells.Values)
            {
                foreach (ShellBinding binding in bindings)
                {
                    if (binding.Shell != null)
                    {
                        Object.Destroy(binding.Shell.gameObject);
                    }
                }
            }

            shells.Clear();
        }

        private void ClearMeshCache()
        {
            foreach (Mesh mesh in expandedMeshes.Values)
            {
                if (mesh != null)
                {
                    Object.Destroy(mesh);
                }
            }

            expandedMeshes.Clear();
        }

        /// <summary>
        /// Creates one shell per rendered mesh below the pile; meshes without a renderer
        /// (colliders, particle systems) are skipped. Inactive children are included on
        /// purpose: a piece the game activates later (MineRock5 chunks) then carries its
        /// shell already, instead of waiting for a resync that never comes.
        /// </summary>
        private void CreateShells(GameObject pile)
        {
            List<ShellBinding> created = new List<ShellBinding>();

            MeshFilter[] filters = pile.GetComponentsInChildren<MeshFilter>(true);
            foreach (MeshFilter filter in filters)
            {
                Mesh source = filter.sharedMesh;
                MeshRenderer sourceRenderer = filter.GetComponent<MeshRenderer>();
                if (source == null || sourceRenderer == null)
                {
                    continue;
                }

                GameObject shell = new GameObject("MuddyScrapPileHighlight_Shell");
                shell.transform.SetParent(filter.transform, false);

                MeshFilter shellFilter = shell.AddComponent<MeshFilter>();
                shellFilter.sharedMesh = ResolveShellMesh(source);

                MeshRenderer shellRenderer = shell.AddComponent<MeshRenderer>();
                shellRenderer.sharedMaterial = shellMaterial;
                shellRenderer.shadowCastingMode = ShadowCastingMode.Off;
                shellRenderer.receiveShadows = false;
                shellRenderer.lightProbeUsage = LightProbeUsage.Off;
                shellRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                shellRenderer.enabled = sourceRenderer.enabled;

                if (!source.isReadable)
                {
                    // No vertex access: grow the shell from the mesh pivot instead.
                    float smallest = Mathf.Max(
                        0.05f,
                        Mathf.Min(source.bounds.size.x, source.bounds.size.y, source.bounds.size.z)
                    );
                    shell.transform.localScale =
                        Vector3.one * (1f + ((2f * appliedWidth) / smallest));
                }

                created.Add(
                    new ShellBinding
                    {
                        Source = sourceRenderer,
                        SourceFilter = filter,
                        ShellFilter = shellFilter,
                        Shell = shellRenderer,
                        Mesh = source,
                        VertexCount = source.vertexCount,
                        Visible = sourceRenderer.enabled,
                    }
                );
            }

            shells[pile] = created;
        }

        /// <summary>
        /// Returns a copy of the source mesh pushed out along its normals, cached because
        /// all piles of a world share the few prefab meshes.
        /// </summary>
        private Mesh ResolveShellMesh(Mesh source)
        {
            if (expandedMeshes.TryGetValue(source, out Mesh cached))
            {
                return cached;
            }

            Mesh expanded = source;
            if (source.isReadable)
            {
                expanded = Object.Instantiate(source);
                expanded.name = source.name + "_MuddyScrapPileHighlight";

                Vector3[] vertices = expanded.vertices;
                Vector3[] normals = expanded.normals;
                bool useNormals = normals != null && normals.Length == vertices.Length;
                Vector3 center = expanded.bounds.center;

                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 direction = useNormals ? normals[i] : (vertices[i] - center).normalized;
                    vertices[i] += direction * appliedWidth;
                }

                expanded.vertices = vertices;
                expanded.RecalculateBounds();
            }

            expandedMeshes[source] = expanded;
            return expanded;
        }

        /// <summary>
        /// Builds the shared shell material the first time it is needed. Shader.Find only
        /// sees shaders whose assets are already loaded, so a miss is retried instead of
        /// latched: the first probe runs in a loaded world, never in the main menu.
        /// </summary>
        private bool EnsureMaterial()
        {
            if (shellMaterial != null)
            {
                return true;
            }

            if (Time.time < nextShaderRetry)
            {
                return false;
            }

            foreach (string shaderName in ShaderCandidates)
            {
                Shader shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    continue;
                }

                string tint = FindTintProperty(shader);
                if (tint == null)
                {
                    log.LogInfo($"Shader {shaderName} has no colour property, skipping it");
                    continue;
                }

                shellMaterial = new Material(shader);
                shellTintProperty = tint;
                appliedColor = config.ShellColor.Value;
                shellMaterial.SetColor(tint, appliedColor);

                // Transparent queue, so opaque geometry that wrote depth (crypt stone,
                // walls, terrain) still occludes the shell. Water is handled by hiding it,
                // never by a later queue - that cost the shell its occlusion.
                shellMaterial.renderQueue = TransparentQueue;
                log.LogInfo(
                    $"Shell shader: {shaderName}, tint property {tint}, "
                        + $"render queue {shellMaterial.renderQueue}"
                );
                return true;
            }

            if (!loggedShaderError)
            {
                loggedShaderError = true;
                log.LogError(
                    "No usable highlight shader found (tried "
                        + $"{string.Join(", ", ShaderCandidates)}); retrying in 30 s."
                );
            }

            nextShaderRetry = Time.time + 30f;
            return false;
        }

        /// <summary>
        /// Hides the crypt's own water (HideCryptWater) because Custom/Water hardcodes its
        /// depth-write and the shell cannot be composited over it, so it would wash the
        /// glow out. Runs on a timer since water loads and unloads with the zones, but a
        /// HideCryptWater toggle is applied right away; every change is put back on
        /// disable, shutdown or edit.
        /// </summary>
        private void SyncWater()
        {
            bool hideCrypt = config.HideCryptWater.Value;

            // HideCryptWater was just toggled: rescan on the next pass instead of waiting
            // out the slow timer, so the water goes away (or comes back) right away. This
            // method only runs while the mod is active - that is, only inside a crypt - so
            // the rescan never happens outside one.
            if (hideCrypt != appliedHideCrypt)
            {
                appliedHideCrypt = hideCrypt;
                nextWaterNet = 0f;
            }

            // Guarded by the count so the default configuration (off) costs a single bool
            // check per frame instead of walking a collection that is always empty.
            if (!hideCrypt && hiddenWater.Count > 0)
            {
                RestoreCryptWater();
            }

            if (!hideCrypt || Time.time < nextWaterNet)
            {
                return;
            }

            nextWaterNet = Time.time + 15f;

            // The crypt water is not a Water component (the game does not register it), so
            // it is found by name. This is a traversal of every renderer in the loaded
            // scenes - by far the most expensive thing in this method - and crypt water
            // only appears and disappears as zones load, so it runs on its own slow timer.
            Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );
            foreach (Renderer renderer in all)
            {
                if (renderer != null && renderer.name.Contains("WaterSurface"))
                {
                    ApplyWaterOptions(renderer, hideCrypt);
                }
            }
        }

        private void ApplyWaterOptions(Renderer renderer, bool hideCrypt)
        {
            if (hideCrypt && IsCryptWater(renderer))
            {
                HideWater(renderer);
            }
        }

        /// <summary>
        /// True for a water surface that belongs to a Sunken Crypt - the only water the
        /// piles are ever mined under, and the only one the shell cannot be drawn over.
        /// </summary>
        private static bool IsCryptWater(Renderer renderer)
        {
            for (Transform up = renderer.transform; up != null; up = up.parent)
            {
                if (up.name.IndexOf("SunkenCrypt", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private void HideWater(Renderer renderer)
        {
            if (!renderer.enabled)
            {
                // Already hidden - by an earlier pass or by the game - and only what this
                // mod turned off is worth putting back.
                return;
            }

            renderer.enabled = false;
            hiddenWater.Add(renderer);

            if (config.DebugLog.Value)
            {
                log.LogInfo($"Hid crypt water '{renderer.name}' so the shell shows");
            }
        }

        /// <summary>Shows again every crypt water surface this mod hid.</summary>
        private void RestoreCryptWater()
        {
            foreach (Renderer renderer in hiddenWater)
            {
                if (renderer != null)
                {
                    renderer.enabled = true;
                }
            }

            hiddenWater.Clear();
        }

        /// <summary>
        /// One-shot debug probe: logs the shader and depth-write of a nearby "stone" piece,
        /// to tell apart the two ways crypt geometry can fail to hide the shell - a shader
        /// that never writes depth, versus a shell that is no longer depth-tested.
        /// </summary>
        private void ProbeOccluder()
        {
            if (occluderProbed || !config.DebugLog.Value)
            {
                return;
            }

            Renderer[] all = UnityEngine.Object.FindObjectsByType<Renderer>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );
            foreach (Renderer renderer in all)
            {
                if (
                    renderer == null
                    || renderer.name.IndexOf("stone", System.StringComparison.OrdinalIgnoreCase) < 0
                )
                {
                    continue;
                }

                Material material = renderer.sharedMaterial;
                if (material == null)
                {
                    continue;
                }

                occluderProbed = true;
                log.LogInfo(
                    $"Occluder probe: '{renderer.name}' shader={material.shader?.name} "
                        + $"queue={material.renderQueue} "
                        + $"has_ZWrite={material.HasProperty("_ZWrite")} "
                        + $"(shell: queue {TransparentQueue}, ZTest LEqual)"
                );
                return;
            }
        }

        /// <summary>The main tint of a shader: "_Color" when present, any other colour otherwise.</summary>
        private static string FindTintProperty(Shader shader)
        {
            int count = shader.GetPropertyCount();
            string fallback = null;

            for (int i = 0; i < count; i++)
            {
                if (shader.GetPropertyType(i) != ShaderPropertyType.Color)
                {
                    continue;
                }

                string name = shader.GetPropertyName(i);
                if (name == "_Color")
                {
                    return name;
                }

                fallback = fallback ?? name;
            }

            return fallback;
        }
    }
}
