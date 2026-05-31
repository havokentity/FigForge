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

        readonly HashSet<int> _styledLists = new HashSet<int>();

        void Awake()
        {
            if (dropdown == null) dropdown = GetComponent<TMP_Dropdown>();
        }

        void OnEnable()
        {
            _styledLists.Clear();
            if (dropdown == null) dropdown = GetComponent<TMP_Dropdown>();
        }

        void LateUpdate()
        {
            var list = FindOpenList();
            if (list == null) return;
            int id = list.GetInstanceID();
            if (_styledLists.Contains(id)) return;
            StyleList(list);
            _styledLists.Add(id);
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
                var states = toggle.GetComponent<FigForgeToggleStateColors>()
                    ?? toggle.GetComponentInChildren<FigForgeToggleStateColors>(true);
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
