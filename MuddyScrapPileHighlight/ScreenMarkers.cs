using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace MuddyScrapPileHighlight
{
    /// <summary>
    /// Draws an on-screen marker over every area still present in the tracked chunks -
    /// one marker per fragment the pickaxe left behind - in OnGUI so terrain, water and
    /// crypt walls cannot hide it: the whole point of a marker over piles that sit in the
    /// dark behind a stone wall. A fragment behind or outside the camera gets an arrow at
    /// the screen edge instead of a dot, aiming at it. A marker disappears with its
    /// fragment, which the game deactivates once mined.
    /// </summary>
    internal class ScreenMarkers
    {
        /// <summary>One mineable area of a chunk: the direct children of the fragment root.</summary>
        private sealed class Area
        {
            internal Transform Node;
            internal MeshFilter Filter;

            /// <summary>
            /// The pile is drawn from the root's combined mesh, so every piece keeps its
            /// own renderer switched off: a piece is still there while its object is
            /// active - mining deactivates it - whatever the renderer says.
            /// </summary>
            internal bool IsPresent()
            {
                return Node != null
                    && Node.gameObject.activeInHierarchy
                    && Filter != null
                    && Filter.sharedMesh != null;
            }

            /// <summary>Where the piece sits, from its own mesh: the pivot is at the root.</summary>
            internal Vector3 WorldCenter()
            {
                return Node.TransformPoint(Filter.sharedMesh.bounds.center);
            }
        }

        private readonly ModConfig config;
        private readonly ManualLogSource log;

        /// <summary>Tracked chunk -> its areas, built once: the game never adds any.</summary>
        private readonly Dictionary<GameObject, Area[]> areaCache =
            new Dictionary<GameObject, Area[]>();

        private GUIStyle distanceStyle;
        private GUIStyle arrowStyle;

        /// <summary>One-shot breadcrumbs: a marker can fail silently in three ways.</summary>
        private bool loggedEntry;
        private bool loggedCameraFallback;
        private bool loggedFirstDraw;
        private readonly HashSet<GameObject> loggedVisibility = new HashSet<GameObject>();

        private Camera fallbackCamera;

        internal ScreenMarkers(ModConfig config, ManualLogSource log)
        {
            this.config = config;
            this.log = log;
        }

        internal void Draw(HashSet<GameObject> tracked)
        {
            if (!loggedEntry && config.DebugLog.Value)
            {
                // Proves OnGUI is called at all - the first silent-failure suspect.
                loggedEntry = true;
                log.LogInfo(
                    $"Markers: OnGUI reached, enabled={config.Marker.Value}, "
                        + $"tracked={tracked.Count}"
                );
            }

            if (!config.Marker.Value || tracked.Count == 0)
            {
                return;
            }

            Camera camera = Camera.main;
            if (camera == null)
            {
                camera = ResolveFallbackCamera();
            }

            if (camera == null)
            {
                return;
            }

            EnsureStyles();

            float size = config.MarkerSize.Value;
            Color color = config.MarkerColor.Value;
            Vector3 origin = camera.transform.position;
            int drawn = 0;

            foreach (GameObject target in tracked)
            {
                if (target == null)
                {
                    continue;
                }

                Area[] areas = AreasOf(target);
                if (areas.Length == 0)
                {
                    if (config.DebugLog.Value && loggedVisibility.Add(target))
                    {
                        log.LogInfo($"Markers: {target.name} has no direct child mesh to mark");
                    }

                    continue;
                }

                bool anyVisible = false;
                foreach (Area area in areas)
                {
                    if (!area.IsPresent())
                    {
                        continue;
                    }

                    anyVisible = true;
                    drawn++;

                    // The piece's own mesh center: the area pivot sits at the chunk root.
                    Vector3 world = area.WorldCenter();
                    Vector3 screen = camera.WorldToScreenPoint(world);
                    string text = $"{Vector3.Distance(origin, world):0} m";

                    Vector2 point = new Vector2(screen.x, Screen.height - screen.y);
                    bool inFront = screen.z > 0f;

                    if (
                        inFront
                        && point.x >= 0f
                        && point.x <= Screen.width
                        && point.y >= 0f
                        && point.y <= Screen.height
                    )
                    {
                        DrawDot(point, size, color);
                        DrawText(point, size, text, color);
                        continue;
                    }

                    // Outside the view - or behind the camera, whose screen point mirrors
                    // the direction - the marker moves to the border with an arrow.
                    Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                    Vector2 offset = inFront ? point - center : center - point;
                    Vector2 edge = center + ClampToBorder(offset, size * 3f);

                    DrawArrow(edge, offset, color);
                    DrawText(edge, size, text, color);
                }

                if (!anyVisible && config.DebugLog.Value && loggedVisibility.Add(target))
                {
                    log.LogInfo(
                        $"Markers: {target.name}: 0/{areas.Length} areas present, sample: "
                            + $"{Describe(areas[0])}"
                    );
                }
            }

            if (drawn > 0 && config.DebugLog.Value && !loggedFirstDraw)
            {
                loggedFirstDraw = true;
                log.LogInfo($"Markers: drawing {drawn} marker(s) with camera {camera.name}");
            }

            PurgeAreaCache(tracked);
        }

        private static string Describe(Area area)
        {
            if (area.Node == null)
            {
                return "node destroyed";
            }

            Mesh mesh = area.Filter != null ? area.Filter.sharedMesh : null;
            return "active="
                + area.Node.gameObject.activeInHierarchy
                + " mesh="
                + (mesh != null ? $"{mesh.name} ({mesh.vertexCount}v)" : "<empty>");
        }

        /// <summary>
        /// Camera.main needs the MainCamera tag: if the game's view camera does not carry
        /// it, the camera closest to the player stands in, so the markers still land.
        /// </summary>
        private Camera ResolveFallbackCamera()
        {
            if (fallbackCamera != null && fallbackCamera.enabled)
            {
                return fallbackCamera;
            }

            Vector3 origin =
                Player.m_localPlayer != null
                    ? Player.m_localPlayer.transform.position
                    : Vector3.zero;

            float bestDepth = float.MinValue;
            float bestSqr = float.MaxValue;
            foreach (Camera camera in Camera.allCameras)
            {
                if (!camera.enabled)
                {
                    continue;
                }

                float sqr = (camera.transform.position - origin).sqrMagnitude;
                bool better =
                    camera.depth > bestDepth
                    || (Mathf.Approximately(camera.depth, bestDepth) && sqr < bestSqr);
                if (better)
                {
                    bestDepth = camera.depth;
                    bestSqr = sqr;
                    fallbackCamera = camera;
                }
            }

            if (fallbackCamera != null && config.DebugLog.Value && !loggedCameraFallback)
            {
                loggedCameraFallback = true;
                log.LogInfo(
                    $"Markers: Camera.main is null, using {fallbackCamera.name} "
                        + $"(depth {fallbackCamera.depth}) instead"
                );
            }

            if (fallbackCamera == null && !loggedCameraFallback)
            {
                loggedCameraFallback = true;
                log.LogWarning("Markers: no camera at all in the scene");
            }

            return fallbackCamera;
        }

        /// <summary>
        /// The areas of a chunk: its direct children that carry a mesh, skipping the root
        /// itself - its mesh is the whole pile - and the shells the highlighter adds.
        /// </summary>
        private Area[] AreasOf(GameObject fragment)
        {
            if (areaCache.TryGetValue(fragment, out Area[] cached))
            {
                return cached;
            }

            List<Area> areas = new List<Area>(32);
            foreach (Transform child in fragment.transform)
            {
                if (
                    child.name.StartsWith(
                        "MuddyScrapPileHighlight",
                        System.StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                MeshFilter filter = child.GetComponent<MeshFilter>();
                if (filter == null)
                {
                    continue;
                }

                areas.Add(new Area { Node = child, Filter = filter });
            }

            Area[] result = areas.ToArray();
            areaCache[fragment] = result;
            return result;
        }

        /// <summary>Forgets the chunks the game destroyed, so the cache stays small.</summary>
        private void PurgeAreaCache(HashSet<GameObject> tracked)
        {
            if (areaCache.Count < 128)
            {
                return;
            }

            List<GameObject> stale = null;
            foreach (KeyValuePair<GameObject, Area[]> pair in areaCache)
            {
                if (pair.Key == null || !tracked.Contains(pair.Key))
                {
                    stale = stale ?? new List<GameObject>();
                    stale.Add(pair.Key);
                }
            }

            if (stale == null)
            {
                return;
            }

            foreach (GameObject entry in stale)
            {
                areaCache.Remove(entry);
                loggedVisibility.Remove(entry);
            }
        }

        /// <summary>Where the direction meets the screen border, inset by the margin.</summary>
        private static Vector2 ClampToBorder(Vector2 offset, float margin)
        {
            if (offset.sqrMagnitude < 0.01f)
            {
                offset = Vector2.up;
            }

            Vector2 direction = offset.normalized;
            float halfWidth = (Screen.width * 0.5f) - margin;
            float halfHeight = (Screen.height * 0.5f) - margin;

            float scale = float.MaxValue;
            if (direction.x != 0f)
            {
                scale = Mathf.Min(scale, halfWidth / Mathf.Abs(direction.x));
            }

            if (direction.y != 0f)
            {
                scale = Mathf.Min(scale, halfHeight / Mathf.Abs(direction.y));
            }

            return direction * scale;
        }

        private static void DrawDot(Vector2 point, float size, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(
                new Rect(point.x - (size * 0.5f), point.y - (size * 0.5f), size, size),
                Texture2D.whiteTexture
            );
            GUI.color = previous;
        }

        private void DrawText(Vector2 point, float size, string text, Color color)
        {
            if (!config.MarkerDistance.Value)
            {
                return;
            }

            Rect rect = new Rect(point.x + size, point.y - size, 100f, size * 2f);

            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.85f);
            GUI.Label(
                new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height),
                text,
                distanceStyle
            );
            GUI.color = color;
            GUI.Label(rect, text, distanceStyle);
            GUI.color = previous;
        }

        /// <summary>An arrow glyph, rotated from "up" towards the fragment's direction.</summary>
        private void DrawArrow(Vector2 point, Vector2 direction, Color color)
        {
            float angle = 90f - (Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);

            Color previous = GUI.color;
            Matrix4x4 previousMatrix = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, point);
            GUI.Label(new Rect(point.x - 12f, point.y - 12f, 24f, 24f), "\u25B2", arrowStyle);
            GUI.matrix = previousMatrix;
            GUI.color = previous;
        }

        /// <summary>Styles built on first use: GUI.skin only exists inside OnGUI.</summary>
        private void EnsureStyles()
        {
            if (distanceStyle != null)
            {
                return;
            }

            distanceStyle = new GUIStyle { fontSize = 12, alignment = TextAnchor.MiddleLeft, };
            distanceStyle.normal.textColor = Color.white;

            arrowStyle = new GUIStyle { fontSize = 18, alignment = TextAnchor.MiddleCenter, };
            arrowStyle.normal.textColor = Color.white;
        }
    }
}
