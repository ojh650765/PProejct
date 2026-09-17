using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PokeLab.UI
{
    public static class UiKeyboardCursor
    {
        // Pass the innermost open panel so covered buttons never enter the tab order.
        public static void Update(Transform scope)
        {
            var keyboard = Keyboard.current;
            var events = EventSystem.current;
            if (keyboard == null || events == null || scope == null) return;
            if (scope.GetComponentInChildren<ConfirmDialog>() != null) return;
            bool tab = keyboard.tabKey.wasPressedThisFrame;
            bool submit = keyboard.fKey.wasPressedThisFrame;
            if (!tab && !submit) return;
            var selected = events.currentSelectedGameObject;
            if (submit && selected != null && selected.GetComponent<TMP_InputField>() != null) return;
            var choices = new List<Selectable>();
            foreach (var candidate in scope.GetComponentsInChildren<Selectable>())
            {
                if (!candidate.IsActive() || !candidate.IsInteractable()) continue;
                bool visible = true;
                foreach (var group in candidate.GetComponentsInParent<CanvasGroup>())
                    if (!group.interactable || !group.blocksRaycasts || group.alpha < 0.05f) visible = false;
                if (visible) choices.Add(candidate);
            }
            if (choices.Count == 0) return;
            int index = choices.FindIndex(x => x.gameObject == selected);
            if (tab || index < 0)
            {
                index = index < 0 ? (keyboard.shiftKey.isPressed ? choices.Count - 1 : 0)
                    : (index + (keyboard.shiftKey.isPressed ? -1 : 1) + choices.Count) % choices.Count;
                var next = choices[index];
                if (next.GetComponent<UiButtonMotion>() == null)
                    UiButtonMotion.Attach(next.transform as RectTransform);
                events.SetSelectedGameObject(next.gameObject);
                UiSound.Navigate();
            }
            if (submit && !tab)
                ExecuteEvents.Execute(choices[index].gameObject, new BaseEventData(events), ExecuteEvents.submitHandler);
        }
    }
}
