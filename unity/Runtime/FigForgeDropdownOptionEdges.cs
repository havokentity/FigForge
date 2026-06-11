// =============================================================================
// FigForge — makes TMP_Dropdown popup rows read as one continuous rounded menu.
//
// TMP clones one Item template for every option. If that template has a rounded
// background, every row becomes a pill. This component waits for the popup list,
// then assigns edge-aware corners to the cloned rows: first gets top corners,
// middle rows are square, last gets bottom corners, single gets all corners.
// =============================================================================

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FigForge
{
    [AddComponentMenu("FigForge/Dropdown Option Edges")]
    public class FigForgeDropdownOptionEdges : MonoBehaviour
    {
        public TMP_Dropdown dropdown;

        Transform _styledList; // popup already styled — alive only while it stays open

        void Awake()
        {
            if (dropdown == null) dropdown = GetComponent<TMP_Dropdown>();
        }

        void OnEnable()
        {
            _styledList = null;
            if (dropdown == null) dropdown = GetComponent<TMP_Dropdown>();
        }

        void LateUpdate()
        {
            // Cheapest gate first: TMP only holds a popup instance while expanded, so a
            // closed dropdown costs one property read per frame — no canvas walk, no
            // allocation. The walk in FindOpenList runs once per popup open; afterwards
            // the cached Transform short-circuits (Unity's == turns it back to null the
            // moment TMP destroys the popup, so a reopened popup is restyled).
            if (dropdown == null || !dropdown.IsExpanded) { _styledList = null; return; }
            if (_styledList != null) return;
            var list = FindOpenList();
            if (list == null) return;
            StyleList(list);
            _styledList = list;
        }

        Transform FindOpenList()
        {
            var canvas = GetComponentInParent<Canvas>();
            var scope = canvas != null ? canvas.transform : transform.root;
            foreach (var t in scope.GetComponentsInChildren<Transform>(true))
                if (t.gameObject.activeInHierarchy && t.name == "Dropdown List")
                    return t;
            return null;
        }

        static void StyleList(Transform list)
        {
            var rows = new List<FigForgeToggleStateColors>();
            foreach (var toggle in list.GetComponentsInChildren<Toggle>(true))
            {
                // No ?? here: in the editor a missing component comes back as Unity's
                // fake-null stub, which ?? treats as found — explicit == null is safe.
                var states = toggle.GetComponent<FigForgeToggleStateColors>();
                if (states == null) states = toggle.GetComponentInChildren<FigForgeToggleStateColors>(true);
                if (states != null && states.useShapeStyles) rows.Add(states);
            }

            rows.Sort((a, b) =>
            {
                var ar = a.GetComponent<RectTransform>();
                var br = b.GetComponent<RectTransform>();
                float ay = ar != null ? ar.position.y : a.transform.position.y;
                float by = br != null ? br.position.y : b.transform.position.y;
                return by.CompareTo(ay);
            });

            for (int i = 0; i < rows.Count; i++)
                rows[i].ApplyListEdgeCorners(i, rows.Count);
        }
    }
}
