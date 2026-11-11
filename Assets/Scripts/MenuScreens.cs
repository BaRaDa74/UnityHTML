using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// Drives the converted HTML screens from a single scene: one UIDocument per
/// screen, one visible at a time.
///
/// Links come from what the converter kept out of the HTML:
///   - data-key="settings"  -> USS class "data-key:settings" on a Button
///   - class="link back-link" -> the Label that was an &lt;a&gt; on the page
///
/// Nothing here is specific to the fixture apart from the screen names.
/// </summary>
[DisallowMultipleComponent]
public class MenuScreens : MonoBehaviour
{
    public UIDocument indexScreen;
    public UIDocument pauseScreen;
    public UIDocument settingsScreen;

    public string Current { get; private set; }

    void Start()
    {
        Bind(indexScreen, "index");
        Bind(pauseScreen, "pause");
        Bind(settingsScreen, "settings");
        Show("index");
    }

    void Bind(UIDocument doc, string screen)
    {
        if (doc == null) return;
        VisualElement root = doc.rootVisualElement;
        if (root == null) return;

        foreach (Button button in root.Query<Button>().ToList())
        {
            string key = DataKey(button);
            if (key == null) continue;
            string owner = screen;
            button.clicked += delegate { Follow(key, owner); };
        }

        // The "back arrow" is an <a> in the HTML, so it survived as a Label.
        foreach (Label label in root.Query<Label>().ToList())
        {
            if (!label.ClassListContains("back-link")) continue;
            string owner = screen;
            // Explicit Action: Clickable(Action) and Clickable(Action<EventBase>)
            // are otherwise ambiguous.
            System.Action clicked = delegate { Follow("main-menu", owner); };
            label.AddManipulator(new Clickable(clicked));
        }
    }

    static string DataKey(VisualElement element)
    {
        foreach (string cls in element.GetClasses())
        {
            if (cls.StartsWith("data-key:")) return cls.Substring("data-key:".Length);
        }
        return null;
    }

    void Follow(string key, string from)
    {
        switch (key)
        {
            case "settings":
                Show("settings");
                break;
            case "main-menu":
                Show("index");
                break;
            // There is no gameplay behind this fixture, so the two ways into a
            // run open the in-run screen, which is the pause menu.
            case "continue":
            case "new-game":
                Show("pause");
                break;
            default:
                Debug.Log("[MenuScreens] '" + key + "' on " + from + " has no screen behind it yet");
                break;
        }
    }

    public void Show(string screen)
    {
        Current = screen;
        SetVisible(indexScreen, screen == "index");
        SetVisible(pauseScreen, screen == "pause");
        SetVisible(settingsScreen, screen == "settings");
    }

    static void SetVisible(UIDocument doc, bool visible)
    {
        if (doc == null || doc.rootVisualElement == null) return;
        // Toggled on the root rather than the GameObject: disabling the
        // UIDocument would rebuild its tree and drop the click handlers.
        doc.rootVisualElement.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
    }
}
