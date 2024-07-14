#if URP || HDRP 
#define SRP
#endif
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.AnimatedValues;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using TreeView = UnityEditor.IMGUI.Controls.TreeView;

#if UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.UIElements;
using See1Studios.See1View;
using static UnityEditor.Profiling.HierarchyFrameDataView;
#endif
#if URP
using UnityEngine.Rendering.Universal;
#endif
#if HDRP
using UnityEngine.Rendering.HighDefinition;
#endif
// base on EditorHelper from Bitstrap (https://assetstore.unity.com/packages/tools/utilities/bitstrap-51416)
public static class EditorHelper
{
    private class ObjectNameComparer : IComparer<Object>
    {
        public readonly static ObjectNameComparer Instance = new ObjectNameComparer();

        int IComparer<Object>.Compare(Object a, Object b)
        {
            return System.String.Compare(a.name, b.name, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Collection of some cool and useful GUI styles.
    /// </summary>
    public static class Styles
    {
        public static GUIStyle Header
        {
            get { return GUI.skin.GetStyle("HeaderLabel"); }
        }

        public static GUIStyle Selection
        {
            get { return GUI.skin.GetStyle("MeTransitionSelectHead"); }
        }

        public static GUIStyle PreDrop
        {
            get { return GUI.skin.GetStyle("TL SelectionButton PreDropGlow"); }
        }

        public static GUIStyle SearchTextField
        {
            get { return GUI.skin.GetStyle("SearchTextField"); }
        }

        public static GUIStyle SearchCancelButtonEmpty
        {
            get { return GUI.skin.GetStyle("SearchCancelButtonEmpty"); }
        }

        public static GUIStyle SearchCancelButton
        {
            get { return GUI.skin.GetStyle("SearchCancelButton"); }
        }

        public static GUIStyle Plus
        {
            get { return GUI.skin.GetStyle("OL Plus"); }
        }

        public static GUIStyle Minus
        {
            get { return GUI.skin.GetStyle("OL Minus"); }
        }

        public static GUIStyle Input
        {
            get { return GUI.skin.GetStyle("flow shader in 0"); }
        }

        public static GUIStyle Output
        {
            get { return GUI.skin.GetStyle("flow shader out 0"); }
        }

        public static GUIStyle Warning
        {
            get { return GUI.skin.GetStyle("CN EntryWarn"); }
        }
    }

    private static string searchField = "";
    private static Vector2 scroll = Vector2.zero;
    private static Texture[] unityIcons = null;

    private static GUIStyle boxStyle = null;

    /// <summary>
    /// The drop down button stored Rect. For use with GenericMenu
    /// </summary>
    public static Rect DropDownRect { get; private set; }

    private static GUIStyle BoxStyle
    {
        get
        {
            if (boxStyle == null)
            {
                boxStyle = EditorStyles.helpBox;

                boxStyle.padding.left = 1;
                boxStyle.padding.right = 1;
                boxStyle.padding.top = 4;
                boxStyle.padding.bottom = 8;

                boxStyle.margin.left = 16;
                boxStyle.margin.right = 16;
            }

            return boxStyle;
        }
    }

    /// <summary>
    /// Begins drawing a box.
    /// Draw its header here.
    /// </summary>
    public static void BeginBoxHeader()
    {
        EditorGUI.BeginChangeCheck();
        EditorGUILayout.BeginVertical(BoxStyle);
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
    }

    /// <summary>
    /// Ends drawing the box header.
    /// Draw its contents here.
    /// </summary>
    public static void EndBoxHeaderBeginContent()
    {
        EndBoxHeaderBeginContent(Vector2.zero);
    }

    /// <summary>
    /// Ends drawing the box header.
    /// Draw its contents here (scroll version).
    /// </summary>
    /// <param name="scroll"></param>
    /// <returns></returns>
    public static Vector2 EndBoxHeaderBeginContent(Vector2 scroll)
    {
        EditorGUILayout.EndHorizontal();
        GUILayout.Space(1.0f);
        return EditorGUILayout.BeginScrollView(scroll);
    }

    /// <summary>
    /// Begins drawing a box with a label header.
    /// </summary>
    /// <param name="label"></param>
    public static void BeginBox(string label)
    {
        BeginBoxHeader();
        Rect rect = GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.label);
        rect.y -= 2.0f;
        rect.height += 2.0f;
        EditorGUI.LabelField(rect, Label(label), Styles.Header);
        EndBoxHeaderBeginContent();
    }

    /// <summary>
    /// Begins drawing a box with a label header (scroll version).
    /// </summary>
    /// <param name="scroll"></param>
    /// <param name="label"></param>
    /// <returns></returns>
    public static Vector2 BeginBox(Vector2 scroll, string label)
    {
        BeginBoxHeader();
        EditorGUILayout.LabelField(Label(label), Styles.Header);
        return EndBoxHeaderBeginContent(scroll);
    }

    /// <summary>
    /// Finishes drawing the box.
    /// </summary>
    /// <returns></returns>
    public static bool EndBox()
    {
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
        return EditorGUI.EndChangeCheck();
    }

    /// <summary>
    /// Reserves a Rect in a layout setup given a style.
    /// </summary>
    /// <param name="style"></param>
    /// <returns></returns>
    public static Rect Rect(GUIStyle style)
    {
        return GUILayoutUtility.GetRect(GUIContent.none, style);
    }

    /// <summary>
    /// Reserves a Rect with an explicit height in a layout.
    /// </summary>
    /// <param name="height"></param>
    /// <returns></returns>
    public static Rect Rect(float height)
    {
        return GUILayoutUtility.GetRect(0.0f, height, GUILayout.ExpandWidth(true));
    }

    /// <summary>
    /// Returns a GUIContent containing a label and the tooltip defined in GUI.tooltip.
    /// </summary>
    /// <param name="label"></param>
    /// <returns></returns>
    public static GUIContent Label(string label)
    {
        return new GUIContent(label, GUI.tooltip);
    }

    /// <summary>
    /// Draws a drop down button and stores its Rect in DropDownRect variable.
    /// </summary>
    /// <param name="label"></param>
    /// <param name="style"></param>
    /// <returns></returns>
    public static bool DropDownButton(string label, GUIStyle style)
    {
        var content = new GUIContent(label);
        DropDownRect = GUILayoutUtility.GetRect(content, style);
        return GUI.Button(DropDownRect, content, style);
    }

    /// <summary>
    /// Draws a search field like those of Project window.
    /// </summary>
    /// <param name="search"></param>
    /// <returns></returns>
    public static string SearchField(string search)
    {
        using (Horizontal.Do())
        {
            search = EditorGUILayout.TextField(search, Styles.SearchTextField);

            GUIStyle buttonStyle = Styles.SearchCancelButtonEmpty;
            if (!string.IsNullOrEmpty(search))
                buttonStyle = Styles.SearchCancelButton;

            if (GUILayout.Button(GUIContent.none, buttonStyle))
                search = "";
        }

        return search;
    }

    /// <summary>
    /// Draws a delayed search field like those of Project window.
    /// </summary>
    /// <param name="search"></param>
    /// <returns></returns>
    public static string DelayedSearchField(string search)
    {
        using (Horizontal.Do())
        {
            search = EditorGUILayout.DelayedTextField(search, Styles.SearchTextField);

            GUIStyle buttonStyle = Styles.SearchCancelButtonEmpty;
            if (!string.IsNullOrEmpty(search))
                buttonStyle = Styles.SearchCancelButton;

            if (GUILayout.Button(GUIContent.none, buttonStyle))
                search = "";
        }

        return search;
    }

    /// <summary>
    /// This is a debug method that draws all Unity styles found in GUI.skin.customStyles
    /// together with its name, so you can later use some specific style.
    /// </summary>
    public static void DrawAllStyles()
    {
        searchField = SearchField(searchField);

        string searchLower = searchField.ToLower(System.Globalization.CultureInfo.InvariantCulture);
        EditorGUILayout.Space();

        using (ScrollView.Do(ref scroll))
        {
            foreach (GUIStyle style in GUI.skin.customStyles)
            {
                if (string.IsNullOrEmpty(searchField) ||
                    style.name.ToLower(System.Globalization.CultureInfo.InvariantCulture).Contains(searchLower))
                {
                    using (Horizontal.Do())
                    {
                        EditorGUILayout.TextField(style.name, EditorStyles.label);
                        GUILayout.Label(style.name, style);
                    }
                }
            }
        }
    }

    /// <summary>
    /// This is a debug method that draws all Unity icons
    /// together with its name, so you can later use them.
    /// </summary>
    public static void DrawAllIcons()
    {
        if (unityIcons == null)
        {
            unityIcons = Resources.FindObjectsOfTypeAll<Texture>();
            System.Array.Sort(unityIcons, ObjectNameComparer.Instance);
        }

        searchField = SearchField(searchField);

        string searchLower = searchField.ToLower(System.Globalization.CultureInfo.InvariantCulture);
        EditorGUILayout.Space();

        using (ScrollView.Do(ref scroll))
        {
            foreach (Texture texture in unityIcons)
            {
                if (texture == null || texture.name == "")
                    continue;

                if (!AssetDatabase.GetAssetPath(texture).StartsWith("Library/"))
                    continue;

                if (string.IsNullOrEmpty(searchField) ||
                    texture.name.ToLower(System.Globalization.CultureInfo.InvariantCulture)
                        .Contains(searchLower))
                {
                    using (Horizontal.Do())
                    {
                        EditorGUILayout.TextField(texture.name, EditorStyles.label);
                        GUILayout.Label(new GUIContent(texture));
                    }
                }
            }
        }
    }

    //Disposables 
    public struct BoxGroup : System.IDisposable
    {
        public static BoxGroup Do(string label)
        {
            EditorHelper.BeginBox(label);
            return new BoxGroup();
        }

        public static BoxGroup Do(ref Vector2 scroll, string label)
        {
            scroll = EditorHelper.BeginBox(scroll, label);
            return new BoxGroup();
        }

        public void Dispose()
        {
            EditorHelper.EndBox();
        }
    }

    public struct DisabledGroup : System.IDisposable
    {
        public static DisabledGroup Do(bool disabled)
        {
            EditorGUI.BeginDisabledGroup(disabled);
            return new DisabledGroup();
        }

        public void Dispose()
        {
            EditorGUI.EndDisabledGroup();
        }
    }

    public struct FadeGroup : System.IDisposable
    {
        public readonly bool visible;

        public static FadeGroup Do(float value)
        {
            var visible = EditorGUILayout.BeginFadeGroup(value);
            return new FadeGroup(visible);
        }

        private FadeGroup(bool visible)
        {
            this.visible = visible;
        }

        public void Dispose()
        {
            EditorGUILayout.EndFadeGroup();
        }
    }

    public struct FieldWidth : System.IDisposable
    {
        private readonly float savedFieldWidth;

        public static FieldWidth Do(float fieldWidth)
        {
            var savedFieldWidth = EditorGUIUtility.fieldWidth;
            EditorGUIUtility.fieldWidth = fieldWidth;

            return new FieldWidth(savedFieldWidth);
        }

        private FieldWidth(float savedFieldWidth)
        {
            this.savedFieldWidth = savedFieldWidth;
        }

        public void Dispose()
        {
            EditorGUIUtility.fieldWidth = savedFieldWidth;
        }
    }

    public sealed class Horizontal : System.IDisposable
    {
        public readonly Rect rect;

        public static Horizontal Do(params GUILayoutOption[] options)
        {
            var rect = EditorGUILayout.BeginHorizontal(options);
            return new Horizontal(rect);
        }

        public static Horizontal Do(GUIStyle style, params GUILayoutOption[] options)
        {
            var rect = EditorGUILayout.BeginHorizontal(style, options);
            return new Horizontal(rect);
        }

        private Horizontal(Rect rect)
        {
            this.rect = rect;
        }

        public void Dispose()
        {
            EditorGUILayout.EndHorizontal();
        }
    }

    public struct IndentLevel : System.IDisposable
    {
        private readonly int savedIndentLevel;

        public static IndentLevel Do(int indentLevel)
        {
            var savedIndentLevel = EditorGUI.indentLevel;
            EditorGUI.indentLevel = indentLevel;

            return new IndentLevel(savedIndentLevel);
        }

        private IndentLevel(int savedIndentLevel)
        {
            this.savedIndentLevel = savedIndentLevel;
        }

        public void Dispose()
        {
            EditorGUI.indentLevel = savedIndentLevel;
        }
    }

    public struct LabelWidth : System.IDisposable
    {
        private readonly float savedLabelWidth;

        public static LabelWidth Do(float labelWidth)
        {
            var savedLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = labelWidth;

            return new LabelWidth(savedLabelWidth);
        }

        private LabelWidth(float savedLabelWidth)
        {
            this.savedLabelWidth = savedLabelWidth;
        }

        public void Dispose()
        {
            EditorGUIUtility.labelWidth = savedLabelWidth;
        }
    }

    public struct Property : System.IDisposable
    {
        public static Property Do(Rect totalPosition, GUIContent label, SerializedProperty property)
        {
            EditorGUI.BeginProperty(totalPosition, label, property);
            return new Property();
        }

        public void Dispose()
        {
            EditorGUI.EndProperty();
        }
    }

    public struct ScrollView : System.IDisposable
    {
        public static ScrollView Do(ref Vector2 scrollPosition, params GUILayoutOption[] options)
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, options);
            return new ScrollView();
        }

        public static ScrollView Do(ref Vector2 scrollPosition, bool alwaysShowHorizontal,
            bool alwaysShowVertical, params GUILayoutOption[] options)
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, alwaysShowHorizontal,
                alwaysShowVertical, options);
            return new ScrollView();
        }

        public static ScrollView Do(ref Vector2 scrollPosition, GUIStyle horizontalScrollbar,
            GUIStyle verticalScrollbar, params GUILayoutOption[] options)
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, horizontalScrollbar,
                verticalScrollbar, options);
            return new ScrollView();
        }

        public static ScrollView Do(ref Vector2 scrollPosition, GUIStyle style,
            params GUILayoutOption[] options)
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, style, options);
            return new ScrollView();
        }

        public static ScrollView Do(ref Vector2 scrollPosition, bool alwaysShowHorizontal,
            bool alwaysShowVertical, GUIStyle horizontalScrollbar, GUIStyle verticalScrollbar,
            GUIStyle background, params GUILayoutOption[] options)
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, alwaysShowHorizontal,
                alwaysShowVertical, horizontalScrollbar, verticalScrollbar, background, options);
            return new ScrollView();
        }

        public void Dispose()
        {
            EditorGUILayout.EndScrollView();
        }
    }

    public struct Vertical : System.IDisposable
    {
        public readonly Rect rect;

        public static Vertical Do(params GUILayoutOption[] options)
        {
            var rect = EditorGUILayout.BeginVertical(options);
            return new Vertical(rect);
        }

        public static Vertical Do(GUIStyle style, params GUILayoutOption[] options)
        {
            var rect = EditorGUILayout.BeginVertical(style, options);
            return new Vertical(rect);
        }

        private Vertical(Rect rect)
        {
            this.rect = rect;
        }

        public void Dispose()
        {
            EditorGUILayout.EndVertical();
        }
    }

    public struct Fade : System.IDisposable
    {
        public static Fade Do(float faded)
        {
            GUI.color = Color.white * faded;
            GUI.backgroundColor = Color.white * faded;
            return new Fade();
        }

        public static Fade Do(Rect r, Color backgroundColor, float faded)
        {
            EditorGUI.DrawRect(r, backgroundColor * faded);
            GUI.color = Color.white * faded;
            GUI.backgroundColor = Color.white * faded;
            return new Fade();
        }

        public void Dispose()
        {
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
        }
    }

    public struct Colorize : System.IDisposable
    {
        public static Colorize Do(Color color, Color bgColor)
        {
            GUI.color = color;
            GUI.backgroundColor = bgColor;
            return new Colorize();
        }

        public static Colorize Do(Rect r, Color color, Color backgroundColor)
        {
            GUI.color = color;
            GUI.backgroundColor = backgroundColor;
            return new Colorize();
        }

        public void Dispose()
        {
            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
        }
    }

    public struct PrefixLabelSize : System.IDisposable
    {
        private readonly Font savedFont;
        private readonly int savedFontSize;

        public static PrefixLabelSize Do(Font font, int fontSize)
        {
            var savedFont = EditorStyles.label.font;
            var savedFontSize = EditorStyles.label.fontSize;
            EditorStyles.label.font = font;
            EditorStyles.label.fontSize = fontSize;

            return new PrefixLabelSize(savedFont, savedFontSize);
        }

        private PrefixLabelSize(Font savedFont, int savedFontSize)
        {
            this.savedFont = savedFont;
            this.savedFontSize = savedFontSize;
        }

        public void Dispose()
        {
            EditorStyles.label.font = savedFont;
            EditorStyles.label.fontSize = savedFontSize;
        }
    }

    //Custom
    public static void GridLayout(int count, int column, Action<int> action)
    {
        using (EditorHelper.Horizontal.Do())
        {
            for (int x = 0; x < column; x++)
            {
                int temp = x;
                using (EditorHelper.Vertical.Do())
                {
                    for (int y = temp; y < count; y += column)
                    {
                        using (EditorHelper.Horizontal.Do())
                        {
                            action(y);
                        }
                    }
                }
            }
        }
    }

    public static readonly string[] vector3Names = { "X", "Y", "Z" };
    public static readonly string[] vector4Names = { "X", "Y", "Z", "W" };

    public static Vector3 FlipAxisDrawer(Vector3 vector, string[] names = null, params GUILayoutOption[] options)
    {
        if (names == null) names = vector3Names;
        float x = vector.x;
        float y = vector.y;
        float z = vector.z;
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            var bgColor = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1f, 0.5f, 0.25f, 1);
            x = GUILayout.Toggle(x == -1f, "X", "ButtonLeft") ? -1 : 1;
            GUI.backgroundColor = new Color(0.5f, 1, 0.25f, 1); ;
            y = GUILayout.Toggle(y == -1f, "Y", "ButtonMid") ? -1 : 1;
            GUI.backgroundColor = new Color(0.25f, 0.5f, 1, 1); ;
            z = GUILayout.Toggle(z == -1f, "Z", "ButtonRight") ? -1 : 1;
            GUI.backgroundColor = bgColor;
            if (EditorGUI.EndChangeCheck())
            {
                vector = new Vector3(x, y, z);
            }
        }
        return vector;
    }

    public static int EnumFlagSelector<T>(int enumValue) where T : Enum
    {
        using (EditorHelper.Horizontal.Do())
        {
            string[] enumNames = Enum.GetNames(typeof(T));
            bool[] buttons = new bool[enumNames.Length];
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                enumValue = GUILayout.Toggle((int)enumValue == 0, "None", EditorStyles.miniButtonLeft)
                    ? 0
                    : enumValue;
                int buttonsValue = 0;
                for (int i = 0; i < buttons.Length; i++)
                {
                    buttons[i] = ((int)enumValue & (1 << i + 1)) == (1 << i + 1);
                    buttons[i] = GUILayout.Toggle(buttons[i], enumNames[i], EditorStyles.miniButtonMid);
                    if (buttons[i])
                    {
                        buttonsValue += 1 << i + 1;
                    }
                }

                if (check.changed)
                {
                    enumValue = buttonsValue;
                }
                if (GUILayout.Button("All", EditorStyles.miniButtonRight))
                {
                    enumValue = ~0;
                }
            }
        }
        return enumValue;
    }


    public static bool Foldout(bool display, string title)
    {
        GUI.backgroundColor = GetDefaultBackgroundColor() * 0.5f;
        var style = new GUIStyle("ShurikenModuleTitle");
        style.font = new GUIStyle(EditorStyles.label).font;
        style.normal.textColor = Color.white;
        style.fontSize = 10;
        style.border = new RectOffset(15, 7, 4, 4);
        style.fixedHeight = 20;
        style.contentOffset = new Vector2(20f, -2f);
        var rect = GUILayoutUtility.GetRect(16f, style.fixedHeight, style);
        GUI.Box(rect, title, style);
        GUI.backgroundColor = Color.white;
        style.margin = new RectOffset(4, 4, 4, 4);
        var e = Event.current;

        var toggleRect = new Rect(rect.x + 4f, rect.y + 2f, 13f, 13f);
        if (e.type == EventType.Repaint)
        {
            EditorStyles.foldout.Draw(toggleRect, false, false, display, false);
        }

        if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
        {
            display = !display;
            e.Use();
        }

        return display;
    }

    public class FoldGroup
    {
        static Dictionary<string, AnimBoolS> dict = new Dictionary<string, AnimBoolS>();

        public static void Do(string label, bool initValue, UnityAction action)
        {
            if (!dict.ContainsKey(label)) dict.Add(label, new AnimBoolS(initValue));
            dict[label].target = EditorHelper.Foldout(dict[label].target, label);
            using (var fade = new EditorGUILayout.FadeGroupScope(dict[label].faded))
            {
                if (fade.visible)
                {
                    action.Invoke();
                }
            }
        }
    }

    public class RectGrid
    {
        static Rect inputRect;
        private const float Width0 = 120;
        private const float Width1 = 0;
        private const float ColSpacing = 5;
        private const float RowSpacing = 5;

        int rowCount;
        int columnCount;
        List<Rect> rectList = new List<Rect>();
        RectOffset offset;
        public RectGrid(Rect position, int rowCount, int columnCount)
        {
            inputRect = position;
            this.rowCount = rowCount;
            this.columnCount = columnCount;
            for (int i = 0; i < rowCount; i++)
            {
                float rowHeight = position.height / rowCount;
                Rect row = new Rect(position.x, position.y + rowHeight * i, position.width, rowHeight);
                for (int j = 0; j < columnCount; j++)
                {
                    float columnWidth = row.width / columnCount;
                    Rect column = new Rect(row.x + columnWidth * j, row.y, columnWidth, row.height);
                    rectList.Add(column);
                }
            }
        }

        public RectGrid(Rect position, float[] rowSizes, float[] columSizes, RectOffset offset)
        {
            this.offset = offset;
            inputRect = position;
            this.rowCount = rowSizes.Length;
            this.columnCount = columSizes.Length;
            for (int i = 0; i < rowCount; i++)
            {
                float prevOffsetY = 0f;
                for (int x = 0; x < i; x++)
                {
                    prevOffsetY += position.height * rowSizes[x];
                }
                float positionY = i == 0 ? position.y : position.y + prevOffsetY;
                float rowHeight = position.height * rowSizes[i];
                Rect row = new Rect(position.x, positionY, position.width, rowHeight);
                for (int j = 0; j < columnCount; j++)
                {
                    float prevOffsetX = 0f;
                    for (int y = 0; y < j; y++)
                    {
                        prevOffsetX += position.width * columSizes[y];
                    }
                    float positionX = j == 0 ? row.x : row.x + prevOffsetX;
                    float columnWidth = position.width * columSizes[j];
                    Rect column = new Rect(positionX, row.y, columnWidth, row.height);
                    rectList.Add(column);
                }
            }
        }

        public Rect Get(int rowIndex, int columnIndex)
        {
            return offset.Remove(rectList[(rowIndex * columnCount) + columnIndex]);
        }
    }

    public static List<string> StringSelector(List<string> result, string[] src)
    {
        if (src != null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < src.Length; i++)
                {
                    bool enabled = result.Contains(src[i]);
                    var style = GUIStyle.none;
                    if (i == 0) style = EditorStyles.miniButtonLeft;
                    else if (i == src.Length - 1) style = EditorStyles.miniButtonRight;
                    else style = EditorStyles.miniButtonMid;
                    enabled = GUILayout.Toggle(enabled, src[i].Replace(".", "").ToUpper(), style,
                        GUILayout.Height(30));
                    if (enabled && !result.Contains(src[i])) result.Add(src[i]);
                    else if (enabled && result.Contains(src[i])) continue;
                    else result.Remove(src[i]);
                }
            }
        }

        return result;
    }

    public static void IconLabel(Type type, string text, int fontSize = 18)
    {
        GUIContent title = new GUIContent(text, EditorGUIUtility.ObjectContent(null, type).image, text);
        var style = new GUIStyle(EditorStyles.label);
        style.fontSize = fontSize;
        style.normal.textColor = Color.gray * 0.75f;
        style.fontStyle = FontStyle.BoldAndItalic;
        style.alignment = TextAnchor.MiddleLeft;
        style.stretchWidth = true;
        style.stretchHeight = true;
        GUILayout.Label(title, style, GUILayout.Width(200), GUILayout.Height(fontSize * 2));
    }

    static Texture2D staticTex;

    public static GUIStyle GetStyle(GUIStyle baseStyle, Color bgColor, int fontSize, FontStyle fontStyle,
        TextAnchor alignment)
    {
        var dragOKstyle = new GUIStyle(GUI.skin.box)
        { fontSize = 10, fontStyle = fontStyle, alignment = alignment };
        staticTex = new Texture2D(1, 1);
        staticTex.hideFlags = HideFlags.HideAndDontSave;
        Color[] colors = new Color[1] { bgColor };
        staticTex.SetPixels(colors);
        staticTex.Apply();
        dragOKstyle.normal.background = staticTex;
        return dragOKstyle;
    }

    public static float GetToolbarHeight()
    {
        return 18;
        //return EditorStyles.toolbar.CalcHeight(GUIContent.none, 0f);
    }

    public static Color GetDefaultBackgroundColor()
    {
        float kViewBackgroundIntensity = EditorGUIUtility.isProSkin ? 0.22f : 0.76f;
        return new Color(kViewBackgroundIntensity, kViewBackgroundIntensity, kViewBackgroundIntensity, 1f);
    }
}
// editor Styles
class Styles
{
    public static GUIStyle centeredBigLabel;
    public static GUIStyle centeredBoldLabel;

    public static GUIStyle header;
    public static GUIStyle blackHeader;
    public static GUIStyle headerCheckbox;
    public static GUIStyle headerFoldout;

    public static GUIStyle miniHeader;
    //public static GUIStyle miniHeaderCheckbox;
    //public static GUIStyle miniHeaderFoldout;

    //public static Texture2D playIcon;
    //public static Texture2D checkerIcon;

    public static GUIStyle miniButton;

    public static GUIStyle transButton;
    //public static GUIStyle miniTransButton;
    //public static GUIStyle transFoldout;

    //public static GUIStyle tabToolBar;

    public static GUIStyle centeredMiniLabel;
    public static GUIStyle centeredMiniBoldLabel;
    public static GUIStyle rightAlignedMiniLabel;
    //public static GUIStyle tabToolBar;

    static Styles()
    {
        centeredBigLabel = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontStyle = FontStyle.Bold,
            fontSize = 24
        };
        centeredBoldLabel = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.UpperCenter,
            fontStyle = FontStyle.Bold
        };

        centeredMiniLabel = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.UpperCenter
        };
        rightAlignedMiniLabel = new GUIStyle(EditorStyles.miniLabel)
        {
            alignment = TextAnchor.MiddleRight
        };
        header = new GUIStyle("ShurikenModuleTitle")
        {
            font = (new GUIStyle("Label")).font,
            border = new RectOffset(15, 7, 4, 4),
            fixedHeight = 22,
            contentOffset = new Vector2(20f, -2f)
        };

        headerCheckbox = new GUIStyle("ShurikenCheckMark");
        headerFoldout = new GUIStyle("Foldout");


        blackHeader = new GUIStyle("AnimationEventTooltip");
        //blackHeader.contentOffset = Vector2.zero;
        //blackHeader.margin = new RectOffset(2, 2, 2, 2);
        //blackHeader.padding = new RectOffset(2, 2, 2, 2);
        blackHeader.overflow = new RectOffset(0, 0, 0, 0);
        miniHeader = new GUIStyle("ShurikenModuleTitle")
        {
            font = (new GUIStyle("Label")).font,
            fontSize = 8,
            fontStyle = FontStyle.Bold,
            border = new RectOffset(15, 7, 4, 4),
            fixedHeight = 18,
            contentOffset = new Vector2(8f, -2f)
        };

        //playIcon = (Texture2D)EditorGUIUtility.LoadRequired(
        //    "Builtin Skins/DarkSkin/Images/IN foldout act.png");
        //checkerIcon = (Texture2D)EditorGUIUtility.LoadRequired("Icons/CheckerFloor.png");

        miniButton = new GUIStyle("miniButton");
        transButton = new GUIStyle("Button");
        //transButton.active.background = Texture2D.blackTexture;
        //transButton.hover.background = Texture2D.blackTexture;
        //transButton.focused.background = Texture2D.blackTexture;
        //transButton.normal.background = Texture2D.blackTexture;
        //transButton.active.textColor = Color.white;
        //transButton.normal.textColor = Color.gray;
        //transButton.onActive.background = Texture2D.blackTexture;
        //transButton.onFocused.background = Texture2D.blackTexture;
        //transButton.onNormal.background = Texture2D.blackTexture;
        //transButton.onHover.background = Texture2D.blackTexture;
        //transButton.fontStyle = FontStyle.Bold;

        //miniTransButton = new GUIStyle("miniButton");
        //miniTransButton.active.background = Texture2D.blackTexture;
        //miniTransButton.hover.background = Texture2D.blackTexture;
        //miniTransButton.focused.background = Texture2D.blackTexture;
        //miniTransButton.normal.background = Texture2D.blackTexture;
        //miniTransButton.onActive.background = Texture2D.blackTexture;
        //miniTransButton.onFocused.background = Texture2D.blackTexture;
        //miniTransButton.onNormal.background = Texture2D.blackTexture;
        //miniTransButton.onHover.background = Texture2D.blackTexture;
        //miniTransButton.active.textColor = Color.white;
        //miniTransButton.normal.textColor = Color.gray;
        //miniTransButton.normal.background = null;
        //miniTransButton.fontStyle = FontStyle.Normal;
        //miniTransButton.alignment = TextAnchor.MiddleLeft;

        //transFoldout = new GUIStyle("Foldout");
        //transFoldout.alignment = TextAnchor.MiddleCenter;
        //transFoldout.contentOffset = Vector2.zero;

        //tabToolBar = new GUIStyle("dragtab");
        ////tabToolBar.onNormal.textColor = Color.white;
        //tabToolBar.fontSize = 9;
        //tabToolBar.alignment = TextAnchor.MiddleCenter;
        centeredMiniLabel = new GUIStyle(EditorStyles.miniLabel);
        centeredMiniLabel.alignment = TextAnchor.MiddleCenter;
        centeredMiniBoldLabel = new GUIStyle(EditorStyles.miniBoldLabel);
        centeredMiniBoldLabel.alignment = TextAnchor.MiddleCenter;
        centeredMiniBoldLabel.padding = new RectOffset(-4, -4, -4, -4);
        rightAlignedMiniLabel = new GUIStyle(EditorStyles.miniBoldLabel);
        rightAlignedMiniLabel.alignment = TextAnchor.MiddleRight;
        //tabToolBar = new GUIStyle("dragtab");
        //tabToolBar.onNormal.textColor = Color.white;
        //tabToolBar.fontSize = 9;
        //tabToolBar.alignment = TextAnchor.MiddleCenter;
    }
}
[Serializable]
class AnimBoolS : BaseAnimValue<bool>
{
    [SerializeField] private float m_Value;

    public AnimBoolS()
        : base(false)
    {
    }

    public AnimBoolS(bool value)
        : base(value)
    {
    }

    public AnimBoolS(UnityAction callback)
        : base(false, callback)
    {
    }

    public AnimBoolS(bool value, UnityAction callback)
        : base(value, callback)
    {
    }

    public float faded
    {
        get
        {
            this.GetValue();
            return this.m_Value;
        }
    }

    protected override bool GetValue()
    {
        float a = !this.target ? 1f : 0.0f;
        float b = 1f - a;
        this.m_Value = Mathf.SmoothStep(a, b, this.lerpPosition);
        return (double)this.m_Value > 0.5;
    }

    public float Fade(float from, float to)
    {
        return Mathf.SmoothStep(from, to, this.faded);
    }
}

// gui helper
class RectSlicer
{
    private EditorWindow window;
    private Rect _rect;

    public Rect rect
    {
        get { return window ? window.position : _rect; }
        set { _rect = value; }
    }

    //EditiorWindow GUI
    public AnimBoolS openTop;
    public AnimBoolS openLeft;
    public AnimBoolS openRight;
    public AnimBoolS openBottom;
    public float topTargetHeight = 100;
    public float bottomTargetHeight = 100;
    public float leftTargetWidth = 200;
    public float rightTargetWidth = 200;

    public float topHeight
    {
        get { return openTop.faded * topTargetHeight; }
    }

    public float bottomHeight
    {
        get { return openBottom.faded * bottomTargetHeight; }
    }

    public float leftWidth
    {
        get { return openLeft.faded * leftTargetWidth; }
    }

    public float rightWidth
    {
        get { return openRight.faded * rightTargetWidth; }
    }

    public Rect center
    {
        get
        {
            return new Rect(leftWidth, topHeight, rect.width - leftWidth - rightWidth,
                rect.height - topHeight - bottomHeight);
        }
    } // { width = rect.width - leftWidth - rightWidth, height = rect.height - topHeight - bottomHeight, x = leftWidth, y = topHeight }; } }

    public Rect top
    {
        get { return new Rect(leftWidth, 0, rect.width - leftWidth - rightWidth, topHeight); }
    } //{ width = rect.width, height = topHeight, x = 0, y = 0 }; } }

    public Rect stretchedTop
    {
        get { return new Rect(0, 0, rect.width, topHeight); }
    } //{ width = rect.width, height = topHeight, x = 0, y = 0 }; } }

    public Rect bottom
    {
        get
        {
            return new Rect(leftWidth, topHeight + center.height, rect.width - leftWidth - rightWidth,
                bottomHeight);
        }
    }

    public Rect stretchedBottom
    {
        get { return new Rect(0, topHeight + center.height, rect.width, bottomHeight); }
    } // { width = rect.width, height = bottomHeight, x = 0, y = topHeight + center.height }; } }

    public Rect left
    {
        get { return new Rect(0, topHeight, leftWidth, center.height); }
    } //{ width = leftWidth, height = center.height, x = 0, y = topHeight }; } }

    public Rect stretchedLeft
    {
        get { return new Rect(0, 0, leftWidth, rect.height); }
    } //{ width = leftWidth, height = center.height, x = 0, y = topHeight }; } }

    public Rect right
    {
        get { return new Rect(leftWidth + center.width, topHeight, rightWidth, center.height); }
    } // { width = rightWidth, height = center.height, x = leftWidth + center.width, y = topHeight }; } }

    public Rect stretchedRight
    {
        get { return new Rect(leftWidth + center.width, 0, rightWidth, rect.height); }
    }

    public Rect full
    {
        get { return new Rect(0, 0, rect.width, rect.height); }
    } // { width = rect.width, height = rect.height, x = 0, y = 0 }; } }

    public RectSlicer()
    {
        this.openTop = new AnimBoolS(false);
        this.openBottom = new AnimBoolS(false);
        this.openLeft = new AnimBoolS(false);
        this.openRight = new AnimBoolS(false);
    }


    public RectSlicer(EditorWindow window)
    {
        this.window = window;
        UnityAction onChangeCallback = window.Repaint;
        this.openTop = new AnimBoolS(false);
        this.openTop.valueChanged.AddListener(onChangeCallback);
        this.openBottom = new AnimBoolS(false);
        this.openBottom.valueChanged.AddListener(onChangeCallback);
        this.openLeft = new AnimBoolS(false);
        this.openLeft.valueChanged.AddListener(onChangeCallback);
        this.openRight = new AnimBoolS(false);
        this.openRight.valueChanged.AddListener(onChangeCallback);
    }

    public RectSlicer(UnityAction onChangeCallback)
    {
        this.openTop = new AnimBoolS(false);
        this.openTop.valueChanged.AddListener(onChangeCallback);
        this.openBottom = new AnimBoolS(false);
        this.openBottom.valueChanged.AddListener(onChangeCallback);
        this.openLeft = new AnimBoolS(false);
        this.openLeft.valueChanged.AddListener(onChangeCallback);
        this.openRight = new AnimBoolS(false);
        this.openRight.valueChanged.AddListener(onChangeCallback);
    }

    public RectSlicer(Rect r, UnityAction onChangeCallback)
    {
        this.rect = r;
        this.openTop = new AnimBoolS(false);
        this.openTop.valueChanged.AddListener(onChangeCallback);
        this.openBottom = new AnimBoolS(false);
        this.openBottom.valueChanged.AddListener(onChangeCallback);
        this.openLeft = new AnimBoolS(false);
        this.openLeft.valueChanged.AddListener(onChangeCallback);
        this.openRight = new AnimBoolS(false);
        this.openRight.valueChanged.AddListener(onChangeCallback);
    }

    public RectSlicer(Rect r, float topHeight, float bottomHeight, float leftWidth, float rightWidth,
        UnityAction onChangeCallback)
    {
        this.rect = r;
        this.openTop = new AnimBoolS(false);
        this.openTop.valueChanged.AddListener(onChangeCallback);
        this.openBottom = new AnimBoolS(false);
        this.openBottom.valueChanged.AddListener(onChangeCallback);
        this.openLeft = new AnimBoolS(false);
        this.openLeft.valueChanged.AddListener(onChangeCallback);
        this.openRight = new AnimBoolS(false);
        this.openRight.valueChanged.AddListener(onChangeCallback);

        this.topTargetHeight = topHeight;
        this.bottomTargetHeight = bottomHeight;
        this.leftTargetWidth = leftWidth;
        this.rightTargetWidth = rightWidth;
    }

    public RectSlicer(Rect r, bool openTop, float topHeight, bool openBottom, float bottomHeight, bool openLeft,
        float leftWidth, bool openRight, float rightWidth, UnityAction onChangeCallback)
    {
        this.rect = r;
        this.openTop = new AnimBoolS(openTop);
        this.openTop.valueChanged.AddListener(onChangeCallback);
        this.openBottom = new AnimBoolS(openBottom);
        this.openBottom.valueChanged.AddListener(onChangeCallback);
        this.openLeft = new AnimBoolS(openLeft);
        this.openLeft.valueChanged.AddListener(onChangeCallback);
        this.openRight = new AnimBoolS(openRight);
        this.openRight.valueChanged.AddListener(onChangeCallback);

        this.topTargetHeight = topHeight;
        this.bottomTargetHeight = bottomHeight;
        this.leftTargetWidth = leftWidth;
        this.rightTargetWidth = rightWidth;
    }
}
// unique data for URP
[Serializable]
public class URPData : ICloneable
{
    public bool renderPostProcessing = true;
    public bool dithering = true;
    public int antialiasing;

    public object Clone()
    {
        return this.MemberwiseClone();
    }
}
// unique data for HDRP
[Serializable]
public class HDRPData : ICloneable
{
    public bool renderPostProcessing = true;
    public bool dithering = true;
    public int antialiasing;

    public object Clone()
    {
        return this.MemberwiseClone();
    }
}
// Container for all data worth saving
[Serializable]
public class PreviewRenderHelperData : ICloneable
{
    public string name;
    // Control
    public int rotSpeed = 3;
    public int zoomSpeed = 3;
    public int panSpeed = 3;
    public int smoothFactor = 3;
    // Size
    public List<Vector2> viewportSizes = new List<Vector2>();
    // Image
    public ImageSaveMode imageSaveMode = ImageSaveMode.Overwrite;
    public bool openSavedImage = true;
    public bool alphaAppliedImage = true;
    public int imageSizeMultiplier = 1;
    // View
    public View lastView = new View(new Vector2(180f, 0f), 0f, Vector3.zero, 30f);
    public List<View> viewList = new List<View>();
    // Environment
    public Color bgColor = new Color(0.3215686f, 0.3215686f, 0.3215686f, 1f);
    public Color ambientSkyColor = Color.gray;
    public ClearFlags clearFlag = ClearFlags.Color;
    public bool autoFloorHeightEnabled = false;
    public float floorHeight = 0f;
    public float floorScale = 10f;
    // Lighting
    public Lighting lastLighting = new Lighting();
    public List<Lighting> lightingList = new List<Lighting>();
    // Shadows
    public bool shadowEnabled = true;
    public float shadowStrength = 1f;
    public float shadowBias = 0.01f;
    //Render
    public CameraType cameraType = CameraType.Game;
    public float renderScale = 2;
    // Custom Render Features
    public Color wireLineColor = Color.white;
    public Color wireFillColor = Color.black;
    public float wireThickness = 0.1f;
    public float wireUseDiscard = 1;
    public bool planeShadowEnabled = true;
    public Color planeShadowColor = Color.gray;
    public bool heightFogEnabled = true;
    public Color heightFogColor = new Color(0, 0, 0, 0.5f);
    public float heightFogHeight = 1;
    // Post Process
    public bool postProcessEnabled = true;
    public URPData urpData = new URPData();
    public HDRPData hdrpData = new HDRPData();
    // Animation
    public List<Steel> steelList = new List<Steel>();
    // Model
    public bool reframeToTarget = true;
    public bool recalculateBound = true;
    public bool forceUpdateComponent = true;
    public ModelCreateMode modelCreateMode = ModelCreateMode.Default;
    public string lastTargetPath = string.Empty;
    public GameObject _lastTarget;
    public GameObject lastTarget

    {
        get
        {
            return _lastTarget
                ? _lastTarget
                : _lastTarget = AssetDatabase.LoadAssetAtPath<GameObject>(lastTargetPath);
        }
        set
        {
            _lastTarget = value;
            lastTargetPath = AssetDatabase.GetAssetPath(value);
        }
    }

    public string cubemapPath = string.Empty;
    private Texture _cubeMap;
    public Texture cubeMap
    {
        get { return _cubeMap ? _cubeMap : _cubeMap = AssetDatabase.LoadAssetAtPath<Cubemap>(cubemapPath); }
        set
        {
            _cubeMap = value;
            cubemapPath = AssetDatabase.GetAssetPath(value);
        }
    }

    private float _cubeMapMipMapBias;
    public float CubeMapMipMapBias
    {
        get { return _cubeMapMipMapBias; }
        set
        {
            _cubeMapMipMapBias = value;
            if (_cubeMap) _cubeMap.mipMapBias = _cubeMapMipMapBias;
        }
    }
    public string profilePath = string.Empty;

    //Post Processing Stack
#if UNITY_POST_PROCESSING_STACK_V2
    private PostProcessProfile _postProcessProfile;

    public PostProcessProfile profile
    {
        get
        {
            return _postProcessProfile
                ? _postProcessProfile
                : _postProcessProfile = AssetDatabase.LoadAssetAtPath<PostProcessProfile>(profilePath);
        }
        set
        {
            _postProcessProfile = value;
            profilePath = AssetDatabase.GetAssetPath(value);
        }
    }

#endif
    //Scriptable RenderPipeline Support.
    public string renderPipelinePath = string.Empty;

    //Tells you the current render pipeline.
    public RenderPipelineMode renderPipelineMode
    {
        get
        {
            RenderPipelineMode mode = RenderPipelineMode.BuiltIn;
            if (renderPipelineAsset != null)
            {
#if URP
                    if (renderPipelineAsset is UniversalRenderPipelineAsset) mode = RenderPipelineMode.Universal;
#endif

#if HDRP
                        if (renderPipelineAsset is HDRenderPipelineAsset) mode = RenderPipelineMode.HighDefinition;
#endif
            }
            return mode;
        }
    }

    private RenderPipelineAsset _renderPipelineAsset;
    public RenderPipelineAsset renderPipelineAsset
    {
        get
        {
            return _renderPipelineAsset ? _renderPipelineAsset : _renderPipelineAsset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(renderPipelinePath);
        }
        set
        {
            _renderPipelineAsset = value;
            renderPipelinePath = AssetDatabase.GetAssetPath(value);
        }
    }
#if URP || HDRP
        private VolumeProfile _volumeProfile;

        public VolumeProfile volumeProfile
        {
            get
            {
                return _volumeProfile
                ? _volumeProfile
                    : _volumeProfile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(profilePath);
            }
            set
            {
                _volumeProfile = value;
                profilePath = AssetDatabase.GetAssetPath(value);
            }
        }
#endif
    public PreviewRenderHelperData(string name)
    {
        this.name = name;
    }

    public object Clone()
    {
        return this.MemberwiseClone();
    }
}

class DataManager
{
    public enum SaveType
    {
        Project,
        UserSetting,
        EditorPreferences,
        Registry
    }

    static SaveType saveType = SaveType.UserSetting;


    private static DataManager _instance;

    public static DataManager instance
    {
        get { return (_instance != null) ? _instance : Load(); }
        set { _instance = value; }
    }

    public List<PreviewRenderHelperData> dataList = new List<PreviewRenderHelperData>();

    public PreviewRenderHelperData current
    {
        get { return dataList[dataIndex]; }
    }


    private int _dataIndex;

    public int dataIndex
    {
        get { return _dataIndex = Mathf.Clamp(_dataIndex, 0, dataList.Count - 1); }
        set { _dataIndex = value; }
    }

    public static string[] dataNames
    {
        get { return instance.dataList.Select((x) => x.name).ToArray(); }
    }

    public static readonly string key = string.Format("{0}.{1}", "com.see1studios.see1view", GetProjectName().ToLower());
    public static readonly string filePrefix = "See1ViewData_";
    public static UnityEvent onDataChanged = new UnityEvent();
    static bool isAddName;
    static bool isEditName;
    private static string inputStr;
    public static bool _isDirty;


    public static string GetPath()
    {
        string targetPath = string.Empty;
        switch (saveType)
        {
            case SaveType.Project:
                targetPath = $"Assets/Editor/";
                break;
            case SaveType.UserSetting:
                targetPath = $"UserSettings/";
                break;
            case SaveType.EditorPreferences:
                targetPath = $"Assets/Editor/";
                break;
            case SaveType.Registry:
                targetPath = $"Assets/Editor/";
                break;
        }
        return targetPath;
    }

    public static string BuildSavePath(string dataName)
    {
        string savePath = GetPath() + $"{filePrefix}{dataName}.json";
        //UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget();
        string systemProjectPath = Application.dataPath.Replace("Assets", "");
        DirectoryInfo di = new DirectoryInfo(systemProjectPath + Path.GetDirectoryName(savePath));
        if (!di.Exists) di.Create();
        return savePath;
    }

    public bool Add(string name)
    {
        bool canAdd = CheckName(name);
        while (!canAdd)
        {
            name += "_1";
            canAdd = CheckName(name);
        }

        PreviewRenderHelperData data = new PreviewRenderHelperData("Default"); // 방어
        data.name = name;
        dataList.Add(data);
        dataIndex = dataList.Count - 1;
        Save();
        return canAdd;
    }

    public bool RemoveCurrent()
    {
        dataList.Remove(dataList[dataIndex]);
        dataIndex -= 1;
        Save();
        return true;
    }

    public bool Remove(string name)
    {
        dataList.Remove(dataList.FirstOrDefault(x => x.name == name));
        dataIndex -= 1;
        Save();
        return true;
    }

    public bool Remove(PreviewRenderHelperData data)
    {
        if (dataList.Contains(data))
        {
            dataList.Remove(data);
            Mathf.Clamp(dataIndex -= 1, 0, dataList.Count);
            return true;
        }
        Save();
        return false;
    }

    private static string[] GetSavedDataFiles()
    {
        return Directory.GetFiles(GetPath(), $"{filePrefix}*.json");
    }

    private static DataManager Load()
    {
        _instance = new DataManager();
        string[] matchingFiles = GetSavedDataFiles();
        if (matchingFiles.Length > 0)
        {
            foreach (var file in matchingFiles)
            {

                string json = File.ReadAllText(file);
                if (!string.IsNullOrEmpty(json))
                {
                    string name = Path.GetFileName(file).Replace(filePrefix, "");
                    PreviewRenderHelperData data = new PreviewRenderHelperData(name);
                    JsonUtility.FromJsonOverwrite(json, data);
                    instance.dataList.Add(data);
                    _isDirty = false;
                }
            }
        }
        else
        {
            _instance.Add("Default");
            SetDirty();
        }
        return _instance;
    }

    public static void Save()
    {
        // 다시 저장할거니까 일단 모든 세이브파일을 지움.
        string[] matchingFiles = GetSavedDataFiles();
        foreach (var file in matchingFiles)
        {
            File.Delete(file);
        }
        foreach (var data in _instance.dataList)
        {
            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(BuildSavePath(data.name), json);
        }
    }

    public static void DeleteAll()
    {
        if (EditorPrefs.HasKey(key))
        {
            if (EditorUtility.DisplayDialog("Removing " + key + "?", "Are you sure you want to " + "delete the editor key " + key + "?, This action cant be undone", "Yes", "No"))
                EditorPrefs.DeleteKey(key);
        }
        else
        {
            EditorUtility.DisplayDialog("Could not find " + key, "Seems that " + key + " does not exists or it has been deleted already, " + "check that you have typed correctly the name of the key.", "Ok");
        }
    }

    public static bool CheckName(string dataName)
    {
        if (string.IsNullOrEmpty(dataName)) return false;
        if (_instance.dataList.Count(x => x.name == dataName) != 0) return false;
        return true;
    }

    public static string GetProjectName()
    {
        string[] s = Application.dataPath.Split('/');
        string projectName = s[s.Length - 2];
        return projectName;
    }

    public static void SetDirty()
    {
        _isDirty = true;
    }

    public static void ConfirmSave()
    {
        if (_isDirty)
        {
            if (EditorUtility.DisplayDialog("", "", "", ""))
            {
                Save();
            }
        }
    }

    public bool Duplicate()
    {
        PreviewRenderHelperData data = current.Clone() as PreviewRenderHelperData;
        bool canDuplicate = data != null;
        if (canDuplicate)
        {
            data.name += "_1";
            canDuplicate = CheckName(data.name);
            if (canDuplicate)
            {
                dataList.Add(data);
                dataIndex = dataList.Count - 1;
                SetDirty();
            }
        }

        return canDuplicate;
    }

    static void ResetInputState()
    {
        isAddName = false;
        isEditName = false;
        inputStr = string.Empty;
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
    }

    internal static void OnManageGUI()
    {
        using (var check = new EditorGUI.ChangeCheckScope())
        {
            int idx = instance.dataIndex;
            bool enterPressed = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return;
            bool escapePressed = Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape;
            float width = 100f;
            if (isAddName || isEditName)
            {
                GUI.SetNextControlName("input");
                inputStr = EditorGUILayout.TextField(inputStr, GUILayout.Width(width));
                if (enterPressed && GUI.GetNameOfFocusedControl() == "input")
                {
                    if (CheckName(inputStr))
                    {
                        if (isAddName)
                        {
                            instance.Add(inputStr);
                        }

                        if (isEditName)
                        {
                            instance.current.name = inputStr;
                        }
                        ResetInputState();
                    }
                    else
                    {
                        ResetInputState();
                    }
                }

                bool focusLost = GUI.GetNameOfFocusedControl() != "input";
                if (focusLost || escapePressed)
                {
                    ResetInputState();
                }
            }
            else
            {
                instance.dataIndex = (int)EditorGUILayout.Popup(instance.dataIndex, dataNames, EditorStyles.toolbarPopup, GUILayout.Width(width));
            }

            if (GUILayout.Button(Icons.plusIcon, EditorStyles.toolbarButton))
            {
                isAddName = true;
                inputStr = "New";
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                EditorGUI.FocusTextInControl("input");
            }

            using (new EditorGUI.DisabledGroupScope(instance.dataList.Count == 1))
            {
                if (GUILayout.Button(Icons.minusIcon, EditorStyles.toolbarButton))
                {
                    if (EditorUtility.DisplayDialog("Confirm", string.Format("{0}{1}{2}", "Delete ", instance.current.name, "?"), "Ok", "Cancel"))
                    {
                        instance.RemoveCurrent();
                    }
                }
            }

            if (GUILayout.Button(Icons.contextIcon, EditorStyles.toolbarButton))
            {
                isEditName = true;
                inputStr = instance.current.name;
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                EditorGUI.FocusTextInControl("input");
            }

            if (check.changed)
            {
                if (idx != instance.dataIndex)
                {
                    onDataChanged.Invoke();
                }
            }
        }
    }
}

public class SDFShadowTool : EditorWindow, IHasCustomMenu
{
    private DataManager dataManager
    {
        get { return DataManager.instance; }
    }
    // current data shortcut
    private PreviewRenderHelperData currentData
    {
        get { return dataManager.current; }
    }

    // main objects
    PreviewRenderUtility _modelViewUtility;
    PreviewRenderUtility _texViewUtility;

    GameObject _tempObj;
    GameObject _tempPickedObject;
    GameObject _mainTarget;
    public GameObject MainTarget => _mainTarget;
    Dictionary<GameObject, GameObject> _targetDic = new Dictionary<GameObject, GameObject>(); //멀티오브젝트 검사용
    ReflectionProbe _probe;

    Transform _lightPivot;
    Renderer _floor;

    // GUI & Control
    RectSlicer _rs;
    Rect _viewPortRect;
    Rect _controlRect;
    Vector2 _scrollPosL;
    Vector2 _scrollPosR;
    bool _isStartDragValid = false;
    float _deltaTime;
    double _lastTimeSinceStartup = 0f;
    const int _labelWidth = 95;
    const int _toolbarHeight = 21; //oldskin 18 newskin 21
    bool _guiEnabled = true;
    bool _overlayEnabled = true;
    AnimBoolS splashEnabled = new AnimBoolS(false);
    AnimBoolS helpEnabled = new AnimBoolS(false);

    // Camera & Render
    public UnityEvent onChangeRenderPipeline = new UnityEvent();
    Transform _camTr;
    Transform _camPivot;
    Vector3 _targetOffset;
    Material _skyMaterial;

    Material _colorMaterial;
    CommandBuffer _colorCommandBuffer;
    bool _colorEnabled;
    private Color _color = Color.white;

    Material _wireMaterial;
    CommandBuffer _wireCommandBuffer;
    bool _wireFrameEnabled;

    Material _shadowMaterial;
    CommandBuffer _shadowCommandBuffer;
    bool _shadowEnabled;

    Material _heightFogMaterial;
    CommandBuffer _heightFogCommandBuffer;

    Material _depthMaterial;
    CommandBuffer _depthCommandBuffer;
    bool _depthEnabled;

    Material _depthNormalMaterial;
    CommandBuffer _depthNormalCommandBuffer;
    bool _depthNormalEnabled;

    float _screenSeparate;

    Material _gridMaterial;
    CommandBuffer _gridCommandBuffer;
    bool _gridEnabled;

    int _gridSize = 100;
    Color _gridColor = new Color(.5f, .5f, .5f, .5f);
    Shader replaceMentShader;

    int _previewLayer;
    const bool _updateFOV = false;

    private float _destFOV;

    Vector2 _destRot = new Vector2(180, 0);

    // Vector2 _destLightRot = new Vector2(180, 0);
    Vector3 _destPivotPos;
    float _destDistance = 1.0f; //destination distance
    float _dist = 1.0f; //current distance
    float _maxDistance = 20.0f;
    float _minDistance = 1.0f;

    bool _autoRotateCamera;
    bool _autoRotateLight;
    int _cameraAutoRotationSpeed;
    int _lightAutoRotationSpeed;
    int _lightRotationIndex;

#if URP
        UniversalAdditionalCameraData _urpCamera;
#endif
#if HDRP
        HDAdditionalCameraData _hdrpCamera;
        HDAdditionalLightData _hdrpLight0;
        HDAdditionalLightData _hdrpLight1;
        HDAdditionalReflectionData _hdrpReflection;
#endif

    #region Unity Events & Callbacks

    void Awake()
    {
    }

    void OnFocus()
    {
    }

    void OnLostFocus()
    {
    }

    //void OnDestroy()
    //{
    //}

    //void OnInspectorUpdate()
    //{
    //}

    //void OnProjectChange()
    //{
    //}

    //void OnHierarchyChange()
    //{
    //}

    void OnEnable()
    {
        // 기본 초기화
        InitPreviewLayerID();
        Create();
        EditorSceneManager.newSceneCreated += this.OnOpenNewScene;
    }

    void OnDisable()
    {
        // 기본 해제
        EditorSceneManager.newSceneCreated -= this.OnOpenNewScene;
        Cleanup();
        // 일단 추가
        GC.Collect();
        Resources.UnloadUnusedAssets();
    }

    void Update()
    {
        SetEditorDeltaTime();
        SetMaterialProperties();
        Repaint();
    }

    private void InitPreviewLayerID()
    {
        var flags = BindingFlags.Static | BindingFlags.NonPublic;
        var propInfo = typeof(Camera).GetProperty("PreviewCullingLayer", flags);
        _previewLayer = (int)propInfo.GetValue(null, new object[0]);
        //Debug.Log(string.Format("{0} : PreviewLayerID is {1}", this.GetType().Name, _previewLayer.ToString()));
    }

    void SetEditorDeltaTime()
    {
        if (Math.Abs(_lastTimeSinceStartup) < float.Epsilon)
        {
            _lastTimeSinceStartup = EditorApplication.timeSinceStartup;
        }

        _deltaTime = (float)(EditorApplication.timeSinceStartup - _lastTimeSinceStartup);
        _lastTimeSinceStartup = EditorApplication.timeSinceStartup;
    }

    void SetMaterialProperties()
    {
        //if (_skyMaterial)
        //{
        //    _skyMaterial.SetTexture("_Tex", currentData.cubeMap);
        //    //_skyMaterial.SetFloat("_Rotation", _preview.lights[0].transform.rotation.eulerAngles.y);
        //}
        //if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
        //{
        //    if (_colorMaterial && _colorEnabled)
        //    {
        //        _colorMaterial.SetColor("_Color", _color);
        //    }

        //    if (_gridMaterial && _gridEnabled)
        //    {
        //        _gridMaterial.SetColor("_Color", _gridColor);
        //    }

        //    if (_wireMaterial && _wireFrameEnabled)
        //    {
        //        _wireMaterial.SetColor("_LineColor", currentData.wireLineColor);
        //        _wireMaterial.SetColor("_FillColor", currentData.wireFillColor);
        //        _wireMaterial.SetFloat("_WireThickness", currentData.wireThickness);
        //        _wireMaterial.SetFloat("UseDiscard", currentData.wireUseDiscard);
        //    }

        //    if (_shadowMaterial && currentData.planeShadowEnabled)
        //    {
        //        _shadowMaterial.SetColor("_ShadowColor", currentData.planeShadowColor);
        //        _shadowMaterial.SetFloat("_PlaneHeight", _targetInfo.bounds.min.y);
        //    }

        //    if (_heightFogMaterial && currentData.heightFogEnabled)
        //    {
        //        _heightFogMaterial.SetColor("_Color", currentData.heightFogColor);
        //        _heightFogMaterial.SetFloat("_Ground", _targetInfo.bounds.min.y);
        //        _heightFogMaterial.SetFloat("_Height", currentData.heightFogHeight);
        //    }

        //    if (_depthMaterial && _viewMode == ViewMode.Depth)
        //    {
        //        _depthMaterial.SetFloat("_Seperate", _screenSeparate);
        //    }

        //    if (_depthNormalMaterial && _viewMode == ViewMode.Normal)
        //    {
        //        _depthNormalMaterial.SetFloat("_Seperate", _screenSeparate);
        //    }
        //}
    }

    void OnGUI()
    {
        if (_modelViewUtility == null) return;
        if (!_modelViewUtility.camera) return;
        GUI.enabled = _guiEnabled;
        using (EditorHelper.LabelWidth.Do(_labelWidth))
        {
            using (EditorHelper.PrefixLabelSize.Do(EditorStyles.miniLabel.font, 10))
            {
                _viewPortRect = _rs.center;
                _controlRect = new Rect(_rs.center.position.x, _rs.center.position.y + _rs.center.size.y - 120,                    _rs.center.size.x, 120);
                ProcessInput();
                OnGUI_Top(_rs.top);
                //OnGUI_ModelViewPort(_rs.stretchedLeft);
                OnGUI_TextureViewport(_viewPortRect);
                OnGUI_Texture(_rs.bottom);
                OnGUI_Control(_rs.stretchedRight);
                //OnGUI_AnimationControl(_controlRect);
                //OnGUI_ParticleSystemControl(_controlRect);
                //OnGUI_Info(_viewPortRect);
                //OnGUI_Log(_viewPortRect);
                if (!_guiEnabled)
                    EditorGUI.DrawRect(_rs.full, Color.black * 0.5f);
                if (_overlayEnabled)
                    EditorGUI.DrawRect(_controlRect, Color.black * 0.1f);

                //OnGUI_Gizmos(_viewPortRect);

                //Splash
                //splashEnabled.target = !_mainTarget;

                //using (EditorHelper.Colorize.Do(Color.white * splashEnabled.faded, Color.white * splashEnabled.faded))
                //{
                    //Rect logoRect = new Rect(_viewPortRect.position + new Vector2(_viewPortRect.size.x * 0.5f, _viewPortRect.size.y * 0.5f) - new Vector2(80f, 64f), new Vector2(160f, 128f));
                    //Rect titleRect = new Rect(logoRect.position + new Vector2(0, logoRect.size.y), GUILayoutUtility.GetRect(GUIContents.title, Styles.centeredBigLabel, GUILayout.Width(160)).size);
                    //Rect versionRect = new Rect(titleRect.position + new Vector2(0, titleRect.size.y), GUILayoutUtility.GetRect(GUIContents.version, Styles.centeredMiniLabel, GUILayout.Width(160)).size);
                    //Rect copyrightRect = new Rect(versionRect.position + new Vector2(0, versionRect.size.y), GUILayoutUtility.GetRect(GUIContents.copyright, Styles.centeredMiniLabel, GUILayout.Width(160)).size);
                    //Rect btnRect = new Rect(copyrightRect.position + new Vector2(55f, copyrightRect.size.y + 10f), new Vector2(50f, 20f));
                    //var logoFaded = (1 - helpEnabled.faded) * splashEnabled.faded;

                    //using (EditorHelper.Colorize.Do(Color.white * logoFaded, Color.white * logoFaded))
                    //{
                    //    using (new EditorGUI.DisabledScope(helpEnabled.target))
                    //    {
                    //        //var logo = EditorGUIUtility.IconContent("d_SceneAsset Icon").image;
                    //        GUI.DrawTexture(logoRect, Initializer.logoTexture, ScaleMode.ScaleToFit, true, 1, new Color(0.85f, 0.85f, 0.85f) * logoFaded, 0, 0);
                    //        EditorGUI.DropShadowLabel(titleRect, GUIContents.startup, Styles.centeredBigLabel);
                    //        EditorGUI.DropShadowLabel(versionRect, GUIContents.version, Styles.centeredMiniLabel);
                    //        EditorGUI.DropShadowLabel(copyrightRect, GUIContents.copyright, Styles.centeredMiniLabel);
                    //        if (GUI.Button(btnRect, "Help", EditorStyles.miniButton))
                    //        {
                    //            helpEnabled.target = true;
                    //        }
                    //    }

                    //}
                    //var helpFaded = helpEnabled.faded * splashEnabled.faded;

                    //using (EditorHelper.Colorize.Do(Color.white * helpFaded, Color.white * helpFaded))
                    //{
                    //    using (new EditorGUI.DisabledScope(!helpEnabled.target))
                    //    {
                    //        Rect helpRect = new Rect(_viewPortRect.position + new Vector2(_viewPortRect.size.x * 0.5f, _viewPortRect.size.y * 0.5f) - new Vector2(200f, 200f), new Vector2(400f, 400f));

                    //        EditorGUILayout.LabelField("Help");
                    //        if (GUI.Button(btnRect, "Back", EditorStyles.miniButton))
                    //        {
                    //            helpEnabled.target = false;
                    //        }
                    //        EditorGUI.DrawRect(helpRect, Color.black * 0.5f);
                    //        //EditorGUI.DropShadowLabel(helpRect, GUIContents.help, Styles.centeredMiniLabel);
                    //    }
                        //EditorGUI.DrawRect(logoRect, Color.red * 0.5f);
                        //EditorGUI.DrawRect(titleRect, Color.green * 0.5f);
                        //EditorGUI.DrawRect(copyrightRect, Color.blue * 0.5f);
                        //EditorGUI.DrawRect(helpRect, Color.black * 0.5f);
                    //}
                //}
            }
        }
    }

    void OnGUI_Top(Rect r)
    {
        //Open Settings Button

        //GUIStyle style = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        //style.normal.textColor = GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f;
        //Rect btnRect = new Rect(r.x, r.y + r.height, r.width, 24);
        //string btn = _rs.openTop.target ? "▲" : "▼";
        //EditorGUI.DropShadowLabel(btnRect, btn, style);
        //_rs.openTop.target = GUI.Toggle(btnRect, _rs.openTop.target, btn, style);

        using (new GUILayout.AreaScope(r))
        {
            using (var top = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.ExpandWidth(true)))
            {
                //                //rs.openRight.target = GUILayout.Toggle(rs.openRight.target, "Panel", EditorStyles.toolbarButton);
                //                //ws.openTop.target = GUILayout.Toggle(ws.openTop.target, "Top", EditorStyles.toolbarButton);
                //                //rs.openBottom.target = GUILayout.Toggle(rs.openBottom.target, "Bottom", EditorStyles.toolbarButton);
                //                //rs.openLeft.target = GUILayout.Toggle(rs.openLeft.target, "Left", EditorStyles.toolbarButton);
                //                //using (var check = new EditorGUI.ChangeCheckScope())
                //                //{
                //                //    //showStartup.target = GUILayout.Toggle(showStartup.target, "Home", EditorStyles.toolbarButton);
                //                //    //if (check.changed)
                //                //    //{
                //                //    //    sidebarChanged.target = false;
                //                //    //    sidebarChanged.target = true;
                //                //    //    EditorUtility.SetDirty(settings);
                //                //    //}
                //                //}
                //                //bool isPreview = settings.current.modelCreateMode == ModelCreateMode.Preview;
                //                //using (EditorHelper.Colorize.Do(Color.white, isPreview ? Color.cyan : Color.white))
                //                //{
                //                //    if (GUILayout.Toggle(isPreview, "Preview", EditorStyles.toolbarButton))
                //                //    {
                //                //        settings.current.modelCreateMode = ModelCreateMode.Preview;
                //                //    }
                //                //}
                //                using (EditorHelper.Colorize.Do(Color.white, Color.cyan))
                //                {
                //                    if (GUILayout.Button("Render", EditorStyles.toolbarButton))
                //                    {
                //                        RenderAndSaveFile();
                //                    }
                //                }
                //                if (GUILayout.Button("Size", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Add Current"), false,
                //                        () => { AddViewportSize(_viewPortRect.size); });
                //                    menu.AddSeparator("");
                //                    for (var i = 0; i < dataManager.current.viewportSizes.Count; i++)
                //                    {
                //                        var size = dataManager.current.viewportSizes[i];
                //                        menu.AddItem(new GUIContent(string.Format("{0}x{1}", size.x, size.y)), false,
                //                            x => { ResizeWindow((Vector2)x); }, size);
                //                    }

                //                    menu.ShowAsContext();
                //                }

                //                if (GUILayout.Button("View", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Add Current"), false,
                //                        () =>
                //                        {
                //                            currentData.viewList.Add(new View(_destRot, _destDistance, _destPivotPos,
                //                                _preview.cameraFieldOfView));
                //                        });
                //                    menu.AddSeparator("");
                //                    for (var i = 0; i < dataManager.current.viewList.Count; i++)
                //                    {
                //                        var view = dataManager.current.viewList[i];
                //                        menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), view.name)), false,
                //                            x => { ApplyView(x as View); }, view);
                //                    }

                //                    menu.ShowAsContext();
                //                }

                //                if (GUILayout.Button("Lighting", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Add Current"), false,
                //                        () => { currentData.lightingList.Add(GetCurrentLighting()); });
                //                    menu.AddSeparator("");
                //                    for (var i = 0; i < dataManager.current.lightingList.Count; i++)
                //                    {
                //                        var lighting = dataManager.current.lightingList[i];
                //                        menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), lighting.name)), false,
                //                            x => { ApplyLighting(x as Lighting); }, lighting);
                //                    }

                //                    menu.ShowAsContext();
                //                }

                //                if (GUILayout.Button("Model", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Pick"), false,
                //                        () =>
                //                        {
                //                            int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                //                            EditorGUIUtility.ShowObjectPicker<GameObject>(null, false, string.Empty, currentPickerWindow);
                //                        });
                //                    menu.AddSeparator("");
                //                    for (var i = 0; i < _recentModel.size; i++)
                //                    {
                //                        var recent = _recentModel.Get(i);
                //                        if (recent)
                //                        {
                //                            menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                //                                x => { AddModel(x as GameObject); }, recent);
                //                        }
                //                    }
                //                    menu.AddSeparator("");
                //                    menu.AddItem(new GUIContent("Clear"), false,
                //                        () =>
                //                        {
                //                            foreach (var target in _targetDic.ToArray())
                //                            {
                //                                if (target.Key)
                //                                {
                //                                    RemoveModel(target.Value);
                //                                }
                //                            }
                //                        });
                //                    menu.ShowAsContext();
                //                }
                //                if (GUILayout.Button("Animation", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Pick"), false,
                //                        () =>
                //                        {
                //                            int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                //                            EditorGUIUtility.ShowObjectPicker<AnimationClip>(null, false, string.Empty, currentPickerWindow);
                //                        });
                //                    menu.AddSeparator("");
                //                    for (var i = 0; i < _recentAnimation.size; i++)
                //                    {
                //                        var recent = _recentAnimation.Get(i);
                //                        if (recent)
                //                        {
                //                            menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                //                                x => { AddAnimationAndPlay(x as AnimationClip); }, recent);
                //                        }
                //                    }

                //                    menu.ShowAsContext();
                //                }
                //                if (GUILayout.Button("Post Process", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Pick"), false,
                //                        () =>
                //                        {
                //                            int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                //                            if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                //                            {
                //#if UNITY_POST_PROCESSING_STACK_V2
                //                                EditorGUIUtility.ShowObjectPicker<PostProcessProfile>(null, false, string.Empty, currentPickerWindow);
                //#endif
                //                            }
                //                            else
                //                            {
                //#if SRP
                //                                    EditorGUIUtility.ShowObjectPicker<VolumeProfile>(null, false, string.Empty, currentPickerWindow);
                //#endif
                //                            }
                //                        });
                //                    menu.AddSeparator("");

                //                    if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                //                    {
                //#if UNITY_POST_PROCESSING_STACK_V2
                //                        for (var i = 0; i < _recentPostProcessProfile.size; i++)
                //                        {
                //                            var recent = _recentPostProcessProfile.Get(i);
                //                            if (recent)
                //                            {
                //                                menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                //                                    x => { SetPostProcessProfile((PostProcessProfile)x); }, recent);
                //                            }
                //                        }
                //#endif
                //                    }
                //                    else
                //                    {
                //#if SRP
                //                            for (var i = 0; i < _recentVolumeProfile.size; i++)
                //                            {
                //                                var recent = _recentVolumeProfile.Get(i);
                //                                if (recent)
                //                                {
                //                                    menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                //                                        x => { SetVolumeProfile((VolumeProfile)x); }, recent);
                //                                }
                //                            }
                //#endif
                //                    }
                //                    menu.AddSeparator("");
                //                    menu.AddItem(new GUIContent("Clear"), false, () =>
                //                    {
                //                        if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                //                        {
                //#if UNITY_POST_PROCESSING_STACK_V2
                //                            dataManager.current.profile = null;
                //#endif
                //                        }
                //                        else
                //                        {
                //#if SRP
                //                                dataManager.current.volumeProfile = null;
                //#endif
                //                            InitializePostProcess();
                //                        }
                //                    });
                //                    menu.ShowAsContext();
                //                }
                //                if (GUILayout.Button("Pipeline", EditorStyles.toolbarDropDown))
                //                {
                //                    var menu = new GenericMenu();
                //                    menu.AddItem(new GUIContent("Pick"), false,
                //                        () =>
                //                        {
                //#if URP || HDRP
                //                                int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                //                                EditorGUIUtility.ShowObjectPicker<RenderPipelineAsset>(null, false, string.Empty, currentPickerWindow);
                //#endif
                //                        });
                //                    menu.AddSeparator("");

                //#if URP || HDRP
                //                        var pipelines = AssetDatabase.FindAssets("t:RenderPipelineAsset").Select(x => AssetDatabase.GUIDToAssetPath(x)).ToList();
                //                        for (var i = 0; i < pipelines.Count; i++)
                //                        {
                //                            var pipeline = (RenderPipelineAsset)AssetDatabase.LoadAssetAtPath(pipelines[i], typeof(RenderPipelineAsset));
                //                            if (pipeline)
                //                            {
                //                                menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), pipeline.name)), false,
                //                                    x =>
                //                                    {
                //                                        dataManager.current.renderPipelineAsset = ((RenderPipelineAsset)x);
                //                                        onChangeRenderPipeline?.Invoke();
                //                                    }, pipeline);
                //                            }
                //                        }
                //#endif
                //                    menu.AddSeparator("");
                //                    menu.AddItem(new GUIContent("Builtin"), false, () =>
                //                    {
                //                        dataManager.current.renderPipelineAsset = null;
                //                        onChangeRenderPipeline?.Invoke();
                //                    });
                //                    menu.ShowAsContext();
                //                }
                //                //Handle Picker
                //                if (Event.current.commandName == "ObjectSelectorUpdated")
                //                {
                //                    var model = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                //                    if (model)
                //                    {
                //                        AddModel(model);
                //                        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                //                    }

                //                    var animation = EditorGUIUtility.GetObjectPickerObject() as AnimationClip;
                //                    if (animation)
                //                    {
                //                        AddAnimationAndPlay(animation);
                //                        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                //                    }
                //                    if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                //                    {
                //#if UNITY_POST_PROCESSING_STACK_V2
                //                        var postProfile = EditorGUIUtility.GetObjectPickerObject() as PostProcessProfile;
                //                        if (postProfile)
                //                        {
                //                            SetPostProcessProfile(postProfile);
                //                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                //                        }
                //#endif
                //                    }
                //                    else
                //                    {
                //#if SRP
                //                            var volumeProfile = EditorGUIUtility.GetObjectPickerObject() as VolumeProfile;
                //                            if (volumeProfile)
                //                            {
                //                                SetVolumeProfile(volumeProfile);
                //                                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                //                            }
                //#endif
                //                    }
                //#if URP || HDRP
                //                        var pipeline = EditorGUIUtility.GetObjectPickerObject() as RenderPipelineAsset;
                //                        if (pipeline)
                //                        {
                //                            dataManager.current.renderPipelineAsset = pipeline;
                //                            InitializePipeline();
                //                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                //                        }
                //#endif
                //                }
                GUILayout.FlexibleSpace();
                DataManager.OnManageGUI();
            }
        }
    }



    void OnGUI_Texture(Rect r)
    {
        //if (IsDocked())
        //    EditorGUI.DrawRect(r, GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f);
        //Open Settings Button

        //GUIStyle style = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        //style.normal.textColor = GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f;
        //Rect btnRect = new Rect(r.x, r.y -24, r.width, 24);
        //string btn = _rs.openBottom.target ? "▼" : "▲";
        //EditorGUI.DropShadowLabel(btnRect, btn, style);
        //_rs.openBottom.target = GUI.Toggle(btnRect, _rs.openBottom.target, btn, style);

        using (new GUILayout.AreaScope(r))
        {
            using (var top = new EditorGUILayout.HorizontalScope(EditorStyles.toolbar, GUILayout.ExpandWidth(true)))
            {
                //rs.openRight.target = GUILayout.Toggle(rs.openRight.target, "Panel", EditorStyles.toolbarButton);
                //ws.openTop.target = GUILayout.Toggle(ws.openTop.target, "Top", EditorStyles.toolbarButton);
                //rs.openBottom.target = GUILayout.Toggle(rs.openBottom.target, "Bottom", EditorStyles.toolbarButton);
                //rs.openLeft.target = GUILayout.Toggle(rs.openLeft.target, "Left", EditorStyles.toolbarButton);
                //using (var check = new EditorGUI.ChangeCheckScope())
                //{
                //    //showStartup.target = GUILayout.Toggle(showStartup.target, "Home", EditorStyles.toolbarButton);
                //    //if (check.changed)
                //    //{
                //    //    sidebarChanged.target = false;
                //    //    sidebarChanged.target = true;
                //    //    EditorUtility.SetDirty(settings);
                //    //}
                //}
                //if (GUILayout.Button("Size", EditorStyles.toolbarDropDown))
                //{
                //    var menu = new GenericMenu();
                //    foreach (var size in settings.viewPortSizes)
                //    {
                //        menu.AddItem(new GUIContent(string.Format("{0}x{1}", size.x, size.y)), false, _ => { viewPortSize = size; ResizeWindow(); }, new PartData(name));
                //    }
                //    menu.ShowAsContext();
                //}
                //viewPortSize.x = (int)EditorGUILayout.Slider(viewPortSize.x, this.minSize.x - rs.right.size.x, this.maxSize.x, GUILayout.Width(200));
                //viewPortSize.y = (int)EditorGUILayout.Slider(viewPortSize.y, this.minSize.y - rs.stretchedTop.size.y - rs.stretchedBottom.size.y, this.maxSize.y, GUILayout.Width(200));
                //if (GUILayout.Button("Set", EditorStyles.toolbarButton))
                //{
                //    ResizeWindow();
                //}
                //GUILayout.FlexibleSpace();

                //int idx = settings.dataIndex;
                //using (var check = new EditorGUI.ChangeCheckScope())
                //{
                //    settings.dataIndex = (int)EditorGUILayout.Popup(settings.dataIndex, settings.dataNames, EditorStyles.toolbarPopup);
                //    if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(20)))
                //    {
                //        string newName = typeof(AS_Data).Name;
                //        AssetUtils.CreateAssetWithEndNameAction<AS_Data>(newName, OnCreateData);
                //    }
                //    using (new EditorGUI.DisabledGroupScope(settings.dataList.Count == 1))
                //    {
                //        if (GUILayout.Button("-", EditorStyles.toolbarButton, GUILayout.Width(20)))
                //        {
                //            if (EditorUtility.DisplayDialog("Confirm", string.Format("{0}{1}{2}", "Delete ", settings.currentData.name, "?"), "Ok", "Cancel"))
                //            {
                //                settings.dataList.Remove(settings.currentData);
                //                AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(settings.currentData));
                //                settings.dataIndex = 0;
                //            }
                //        }
                //    }
                //    if (check.changed)
                //    {
                //        if (idx != settings.dataIndex)
                //        {
                //            DataChanged();
                //            SidebarChanged();
                //        }
                //    }
                //}
            }
        }
    }

    void OnGUI_TextureViewport(Rect r)
    {
        if (Event.current.type != EventType.Repaint) return;
        if (r.size.x < 0 || r.size.y < 0) return;
        if (!_modelViewUtility.camera.gameObject.activeInHierarchy) return;
        Rect renderRectScaled = new Rect(r.position, r.size);
        GUIStyle style = GUIStyle.none;
        bool enableSRP = currentData.renderPipelineMode != RenderPipelineMode.BuiltIn;

            //using (new QualitySettingsOverrider())
            //{
                //using (new ShowObjectScope(_shadowGo))
                //{
                _modelViewUtility.BeginPreview(renderRectScaled, style);
                //using (new RenderSettingsOverrider(AmbientMode.Flat, currentData.ambientSkyColor, _skyMaterial))
                //{
                    //GL.wireframe = true;
                    //_preview.DrawMesh(Grid.Get(100), Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one),
                    //    _gridMaterial, 0);
                    //GL.wireframe = false;
                    _modelViewUtility.Render(enableSRP, _updateFOV);
               // }

                //Texture image = _preview.EndPreview();
                //GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                //GUI.DrawTexture(r, image, ScaleMode.StretchToFill, true);
                //GL.sRGBWrite = false;
                //UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                _modelViewUtility.EndAndDrawPreview(_viewPortRect);
                //}
            //}
        
        //Grid.Draw(_preview.camera, 100, Color.white);
    }

    //void OnGUI_ModelViewPort(Rect r)
    //{
    //    //Open Settings Button
    //    GUIStyle style = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
    //    //style.normal.textColor = GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f;
    //    Rect btnRect = new Rect(r.width, r.y, 24, r.height);
    //    string btn = _rs.openLeft.target ? "◄" : "►";
    //    EditorGUI.DropShadowLabel(btnRect, btn, style);
    //    _rs.openLeft.target = GUI.Toggle(btnRect, _rs.openLeft.target, btn, style);

    //    Rect area = new RectOffset(0, 0, 0, 0).Remove(r);
    //    using (new GUILayout.AreaScope(area))
    //    {

    //        //using (var check = new EditorGUI.ChangeCheckScope())
    //        //{
    //        //    leftPanelMode = (LeftPanelMode)GUILayout.Toolbar((int)leftPanelMode, Enum.GetNames(typeof(LeftPanelMode)), EditorStyles.toolbarButton);
    //        //    if (check.changed)
    //        //    {
    //        //    }
    //        //}
    //        //switch (leftPanelMode)
    //        //{
    //        //    case LeftPanelMode.Transform:
    //        //        _transformTreeView.searchString = _treeViewSearchField.OnToolbarGUI(_transformTreeView.searchString);
    //        //        break;
    //        //    case LeftPanelMode.Render:
    //        //        _renderTreeView.searchString = _treeViewSearchField.OnToolbarGUI(_renderTreeView.searchString);
    //        //        break;
    //        //}
    //        //using (var svScope = new GUILayout.ScrollViewScope(_scrollPosL))
    //        //{
    //        //    _scrollPosL = svScope.scrollPosition;
    //        //    switch (leftPanelMode)
    //        //    {
    //        //        case LeftPanelMode.Transform:
    //        //            if (_transformTreeView != null)
    //        //            {
    //        //                _transformTreeView.OnGUI(area);
    //        //            }
    //        //            break;
    //        //        case LeftPanelMode.Render:
    //        //            if (_renderTreeView != null)
    //        //            {
    //        //                _renderTreeView.OnGUI(area);
    //        //            }
    //        //            break;
    //        //    }
    //        //}
    //        //if (GUILayout.Button("Unlock Inspector", EditorStyles.toolbarButton))
    //        //{
    //        //    UnlockInspector();
    //        //}
    //    }
    //}

    void OnGUI_Control(Rect r)
    {
        //Open Settings Button
        GUIStyle style = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
        //style.normal.textColor = GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f;
        Rect btnRect = new Rect(r.x - 24, r.y, 24, r.height);
        string btn = _rs.openRight.target ? "►" : "◄";
        EditorGUI.DropShadowLabel(btnRect, btn, style);
        _rs.openRight.target = GUI.Toggle(btnRect, _rs.openRight.target, btn, style);
        Rect area = new RectOffset(0, 0, 0, 0).Remove(r);
        using (EditorHelper.FieldWidth.Do(30))
        {
            using (EditorHelper.Fade.Do(_rs.openRight.faded))
            {
                using (new GUILayout.AreaScope(area))
                {
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        //rightPanelMode = (RightPanelMode)GUILayout.Toolbar((int)rightPanelMode,
                        //    Enum.GetNames(typeof(RightPanelMode)), EditorStyles.toolbarButton);
                        //if (check.changed)
                        //{
                        //}
                    }

                    using (var svScope = new GUILayout.ScrollViewScope(_scrollPosR))
                    {
                        _scrollPosR = svScope.scrollPosition;
                        //switch (rightPanelMode)
                        //{
                        //    case RightPanelMode.View:
                        //        OnGUI_View();
                        //        break;
                        //    case RightPanelMode.Model:
                        //        OnGUI_Model();
                        //        break;
                        //    case RightPanelMode.Animation:
                        //        OnGUI_Animation();
                        //        break;
                        //    case RightPanelMode.Misc:
                        //        OnGUI_Misc();
                        //        break;
                        //}
                        if (GUILayout.Button("Load"))
                        {
                            var go = Selection.activeGameObject;
                            if (go != null)
                                AddModel(go);

                        }
                        if (GUILayout.Button("Primitives", EditorStyles.popup))
                        {
                            var menu = new GenericMenu();
                            menu.AddItem(new GUIContent("Sphere"), false, () =>
                            {
                                var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                                AddModel(primitive);
                                DestroyImmediate(primitive);
                            });
                            menu.AddItem(new GUIContent("Capsule"), false, () =>
                            {
                                var primitive = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                                AddModel(primitive);
                                DestroyImmediate(primitive);
                            });
                            menu.AddItem(new GUIContent("Cylinder"), false, () =>
                            {
                                var primitive = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                                AddModel(primitive);
                                DestroyImmediate(primitive);
                            });
                            menu.AddItem(new GUIContent("Cube"), false, () =>
                            {
                                var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
                                AddModel(primitive);
                                DestroyImmediate(primitive);
                            });
                            menu.AddItem(new GUIContent("Plane"), false, () =>
                            {
                                var primitive = GameObject.CreatePrimitive(PrimitiveType.Plane);
                                AddModel(primitive);
                                DestroyImmediate(primitive);
                            });
                            menu.AddItem(new GUIContent("Quad"), false, () =>
                            {
                                var primitive = GameObject.CreatePrimitive(PrimitiveType.Quad);
                                AddModel(primitive);
                                DestroyImmediate(primitive);
                            });
                            menu.ShowAsContext();
                        }
                    }
                }
            }
        }
    }


    void OnSelectionChange()
    {
        //if (!(currentData.modelCreateMode == ModelCreateMode.Preview)) return;
        //if (Validate(Selection.activeGameObject) == false) return;
        //_tempObj = Selection.activeGameObject;
        //AddModel(_tempObj, true);
    }

    void ProcessInput()
    {
        var axis0 = Vector2.zero;
        var axis1 = Vector2.zero;
        var axis2 = Vector2.zero;
        var zoom = 0.0f;
        var evt = Event.current;
        Rect inputEnabledArea = new Rect(_rs.center.position, new Vector2(_rs.center.width, _rs.center.height - _controlRect.height));
        var isLDragging = evt.type == EventType.MouseDrag && evt.button == 0 && _isStartDragValid;
        var isRDragging = evt.type == EventType.MouseDrag && evt.button == 1 && _isStartDragValid;
        var isMDragging = evt.type == EventType.MouseDrag && evt.button == 2 && _isStartDragValid;
        var isScrolling = evt.type == EventType.ScrollWheel && inputEnabledArea.Contains(evt.mousePosition);
        var isLDoubleClicked = evt.isMouse && evt.type == EventType.MouseDown && evt.button == 0 && evt.clickCount == 2 && inputEnabledArea.Contains(evt.mousePosition);
        var isRDoubleClicked = evt.isMouse && evt.type == EventType.MouseDown && evt.button == 1 && evt.clickCount == 2 && inputEnabledArea.Contains(evt.mousePosition);
        if (evt.type == EventType.MouseDown)
        {
            GUI.FocusControl(null); //Text Field Defocus
            _isStartDragValid = !_rs.right.Contains(evt.mousePosition) && inputEnabledArea.Contains(evt.mousePosition);
        }

        if (evt.type == EventType.MouseUp)
        {
            isLDragging = false;
            isRDragging = false;
            isMDragging = false;
            _isStartDragValid = false;
        }

        Vector2 input = evt.delta.normalized; // settings.mouseAccelerationEnabled ? evt.delta * 0.1f : evt.delta.normalized;
        if (isLDragging) axis0 = input;
        if (isRDragging) axis1 = input;
        if (isMDragging) axis2 = input;
        if (isScrolling) zoom = evt.delta.y;
        axis0 *= currentData.rotSpeed;
        axis2 *= currentData.panSpeed;
        zoom *= currentData.zoomSpeed;
        UpdateCamera(axis0, axis2, zoom);
        UpdateLight(axis1);
    }
    void FitTargetToViewport()
    {
        if (_mainTarget)
        {
            CalcMinMaxDistance();
            //_destPivotPos = _targetInfo.bounds.center;
            //_destDistance = GetFitDistanceOfCamera(_targetInfo.bounds, _preview.camera);
        }
    }

    void CalcMinMaxDistance()
    {
        //if (_mainTarget)
        //{
        //    Vector3 size = _targetInfo.bounds.max - _targetInfo.bounds.min;
        //    float largestSize = Mathf.Max(size.x, size.y, size.z);
        //    float distance = GetFitDistanceOfCamera(_targetInfo.bounds, _preview.camera);
        //    _minDistance = distance * 0.01f;
        //    _maxDistance = largestSize * 100f;
        //    SetClipPlane();
        //}
    }

    float GetFitDistanceOfCamera(Bounds targetBounds, Camera camera)
    {
        float cameraDistance = 1.0f; // 3.0f; // Constant factor
        Vector3 size = targetBounds.max - targetBounds.min;
        float largestSize = Mathf.Max(size.x, size.y, size.z);
        float cameraView = 2.0f * Mathf.Tan(0.5f * Mathf.Deg2Rad * camera.fieldOfView); // Visible height 1 meter in front
        float distance = cameraDistance * largestSize / cameraView; // Combined wanted distance from the object
        distance += 0.1f * largestSize; // Estimated offset from the center to the outside of the object
        return distance;
    }

    void SetClipPlane()
    {
        _modelViewUtility.camera.nearClipPlane = _dist * 0.1f;
        _modelViewUtility.camera.farClipPlane = _maxDistance * 2;
    }

    void UpdateCamera(Vector2 axis0, Vector2 axis2, float wheel)
    {
        float smoothFactor = Mathf.Lerp(10f, 1f, currentData.smoothFactor * 0.2f);

        //ROTATE
        var rotationFactor = axis0; // * Mathf.Pow(currentData.rotSpeed, 2);
        _destRot += rotationFactor;
        _destRot.x = ClampAngle(_destRot.x, -360.0f, 360.0f);
        _destRot.y = ClampAngle(_destRot.y, -90.0f, 90.0f);
        var rotation = _camTr.rotation;
        rotation = Quaternion.Slerp(rotation, Quaternion.Euler(_destRot.y, _destRot.x, 0),
            _deltaTime * smoothFactor);
        _camTr.rotation = rotation;

        //PAN
        var panFactor = new Vector2(-axis2.x, axis2.y) * (_dist * 0.002f);
        _camPivot.rotation = rotation;
        _destPivotPos += _camPivot.rotation * panFactor;
        var pivotPos = _camPivot.position;
        pivotPos = Vector3.Slerp(pivotPos, _destPivotPos, _deltaTime * smoothFactor);
        _camPivot.position = pivotPos;

        //Zoom
        var zoomFactor = wheel * Mathf.Abs(_destDistance) * 0.01f;
        _destDistance += zoomFactor;
        _destDistance = Mathf.Clamp(_destDistance, _minDistance, _maxDistance);
        _dist = Mathf.Lerp(_dist, _destDistance, _deltaTime * smoothFactor);

        //FOV
        _modelViewUtility.cameraFieldOfView = Mathf.Lerp(_modelViewUtility.cameraFieldOfView, _destFOV, _deltaTime * smoothFactor);

        //Final
        _camTr.position = pivotPos - (rotation * Vector3.forward * _dist + _targetOffset);
        SetClipPlane();

        //Ortho
        if (_modelViewUtility.camera.orthographic)
        {
            _modelViewUtility.camera.orthographicSize = _destDistance * _modelViewUtility.cameraFieldOfView * 0.01f;
        }
    }

    void UpdateLight(Vector2 axis)
    {
        var angle = new Vector3(axis.y, -axis.x, 0) * currentData.rotSpeed;
        for (int i = 0; i < _lightRotationIndex + 1; i++)
        {
            var lightTr = _modelViewUtility.lights[i].transform;
            lightTr.Rotate(angle, Space.World);
        }
    }

    void ResetLight()
    {
        _modelViewUtility.lights[0].transform.rotation = Quaternion.identity;
        _modelViewUtility.lights[0].color = new Color(0.769f, 0.769f, 0.769f, 1.0f);
        _modelViewUtility.lights[0].intensity = 1;
        _modelViewUtility.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
        _modelViewUtility.lights[1].color = new Color(0.28f, 0.28f, 0.315f, 1.0f);
        _modelViewUtility.lights[1].intensity = 1;

        var angle = new Vector3(0, -180, 0);

        for (int i = 0; i < _modelViewUtility.lights.Length; i++)
        {
            _modelViewUtility.lights[i].cullingMask = ~_previewLayer;
            var lightTr = _modelViewUtility.lights[i].transform;
            lightTr.Rotate(angle);

            _modelViewUtility.lights[i].shadows =
                currentData.shadowEnabled && i == 0 ? LightShadows.Soft : LightShadows.None;
            _modelViewUtility.lights[i].shadowResolution = LightShadowResolution.VeryHigh;
            _modelViewUtility.lights[i].shadowBias = 0.01f;
        }

        _modelViewUtility.ambientColor = currentData.ambientSkyColor = Color.gray;
    }
    void OnOpenNewScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
    {
        Create();
    }

    #endregion

    public void AddModel(GameObject src)
    {
        AddModel(src, true);
    }

    //메인오브젝트 이외에 하이어라키를 열어 강제로 오브젝트를 추가할 용도.
    public void AddModel(GameObject src, bool isMain = true)
    {
        if (!src) return;
        // 게임오브젝트가 아니면 패스
        if (src.GetType() != typeof(GameObject)) return;
        // 이미 서브모델에 포함된 모델이면 패스
        if (_targetDic.ContainsKey(src)) return;
        // 메인모델이면 서브모델들도 청소합니다.
        if (isMain)
        {
            foreach (var target in _targetDic)
            {
                if (target.Value) DestroyImmediate(target.Value);
            }
            _targetDic.Clear();
        }
        // 소스를 인스턴스화
        bool isPrefab = PrefabUtility.IsPartOfAnyPrefab(src);
        GameObject instance = null;
        if (isPrefab)
        {
            // 소스가 프리팹이면 여기에서 적절하게 처리.
            instance = PrefabUtility.InstantiatePrefab(src) as GameObject;
            //PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction); 
        }
        else
        {
            // 아니면 그냥 인스턴티에이트
            instance = GameObject.Instantiate(src);
        }
        // 인스턴스화가 성공적이면 실제로 씬에 투입하기 위한 준비를 해요
        if (instance != null)
        {
            _targetDic.Add(src, instance);
            if (isMain)
            {
                // 메인모델인 경우 적절하게 처리
                _mainTarget = instance;
            }
            instance.name = src.name;
            SetFlagsAll(instance, HideFlags.HideAndDontSave);
            SetLayerAll(instance, _previewLayer);
            _modelViewUtility.AddSingleGO(instance);
            //_targetInfo.Init(src, instance);
            //_transformTreeView?.Reload();
            //_renderTreeView?.Reload();
            //_particlePlayer.Init(_targetInfo.particleSystems);
            //InitAnimationPlayer(_mainTarget, true);
            //ApplyModelCommandBuffers();
            //if (currentData.forceUpdateComponent)
            //{
            //    foreach (var b in _targetInfo.behaviours)
            //    {
            //        RunInEditHelper2.Add(b);
            //    }
            //    RunInEditHelper2.Start();
            //}
            // 마무리
            Repaint();
            //if (dataManager.current.reframeToTarget) FitTargetToViewport();
            //_recentModel.Add(_targetInfo.assetPath);
            //Notice.Log(string.IsNullOrEmpty(_targetInfo.assetPath) ? src.name : _targetInfo.assetPath, false);
        }
    }

    public void RemoveModel(GameObject instance)
    {
        string name = instance.name;
        if (!_targetDic.ContainsValue(instance)) return;
        if (instance) DestroyImmediate(instance);
        var pair = _targetDic.Where(x => x.Value == instance).FirstOrDefault();
        if (pair.Key)
        {
            _targetDic.Remove(pair.Key);
        }
        //if (_transformTreeView != null)
        //{
        //    _transformTreeView.Reload();
        //}
        //if (_renderTreeView != null)
        //{
        //    _renderTreeView.Reload();
        //}
        //RunInEditHelper2.Clean();
        //Notice.Log(string.Format("{0} Removed", name), false);
        //ResetAnimationPlayer();
        //ApplyModelCommandBuffers();
        Repaint();
    }

    void Create()
    {
        Cleanup();
        _rs = new RectSlicer(this);
        _rs.topTargetHeight = _toolbarHeight; //Styles.GetToolbarHeight();
        _rs.bottomTargetHeight = _toolbarHeight; //Styles.GetToolbarHeight();
        _rs.leftTargetWidth = 0;
        _rs.rightTargetWidth = 250;
        _rs.bottomTargetHeight = 250;
        _rs.openTop.target = true;
        _rs.openBottom.target = true;
        _rs.openLeft.target = false;
        _rs.openRight.target = true;

        //_sizePopup = new SizePopup();
        _modelViewUtility = new PreviewRenderUtility(true, true);
        _camTr = _modelViewUtility.camera.transform;

        var camPivotGo = EditorUtility.CreateGameObjectWithHideFlags("CamPivot", HideFlags.HideAndDontSave);
        _camPivot = camPivotGo.transform;
        _modelViewUtility.AddSingleGO(camPivotGo);

        _modelViewUtility.camera.cullingMask = ~_previewLayer;
        _modelViewUtility.camera.fieldOfView = 30.0f;
        _modelViewUtility.camera.nearClipPlane = 0.5f;
        _modelViewUtility.camera.farClipPlane = 30;
        _modelViewUtility.camera.clearFlags = CameraClearFlags.Color;
        _modelViewUtility.camera.backgroundColor = Color.black;
        _modelViewUtility.camera.allowMSAA = true;
        _modelViewUtility.camera.allowDynamicResolution = true;
        _modelViewUtility.camera.allowHDR = true;
        _modelViewUtility.camera.cameraType = CameraType.Preview;
        _modelViewUtility.ambientColor = Color.gray;
        _modelViewUtility.camera.gameObject.layer = _previewLayer;
    }


    void Cleanup()
    {
        if (_camPivot) DestroyImmediate(_camPivot.gameObject);
        if (_lightPivot) DestroyImmediate(_lightPivot.gameObject);
        if (_floor) DestroyImmediate(_floor.gameObject);

        if (_skyMaterial) DestroyImmediate(_skyMaterial);
        if (_modelViewUtility != null)
        {
            RenderTexture.active = null;
            _modelViewUtility.Cleanup(); //Handle.SetCamera 에서 RenderTexure.active 관련 warning 발생
        }

        if (_gridMaterial) DestroyImmediate(_gridMaterial);
        if (_gridCommandBuffer != null)
        {
            _gridCommandBuffer.Dispose();
            _gridCommandBuffer = null;
        }

        if (_wireMaterial) DestroyImmediate(_wireMaterial);
        if (_wireCommandBuffer != null)
        {
            _wireCommandBuffer.Dispose();
            _wireCommandBuffer = null;
        }

        if (_shadowMaterial) DestroyImmediate(_shadowMaterial);
        if (_shadowCommandBuffer != null)
        {
            _shadowCommandBuffer.Dispose();
            _shadowCommandBuffer = null;
        }

        if (_heightFogMaterial) DestroyImmediate(_heightFogMaterial);
        if (_heightFogCommandBuffer != null)
        {
            _heightFogCommandBuffer.Dispose();
            _heightFogCommandBuffer = null;
        }

        if (_depthMaterial) DestroyImmediate(_depthMaterial);
        if (_depthCommandBuffer != null)
        {
            _depthCommandBuffer.Dispose();
            _depthCommandBuffer = null;
        }


        if (_depthNormalMaterial) DestroyImmediate(_depthNormalMaterial);
        if (_depthNormalCommandBuffer != null)
        {
            _depthNormalCommandBuffer.Dispose();
            _depthNormalCommandBuffer = null;
        }

        if (_colorMaterial) DestroyImmediate(_colorMaterial);
        if (_colorCommandBuffer != null)
        {
            _colorCommandBuffer.Dispose();
            _colorCommandBuffer = null;
        }
    }


    static void SetFlagsAll(GameObject parent, HideFlags flags)
    {
        if (parent)
        {
            var transforms = parent.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < transforms.Length; i++)
            {
                transforms[i].gameObject.hideFlags = flags;
            }
        }
    }

    static void SetLayerAll(GameObject parent, int layer)
    {
        if (parent)
        {
            var renderers = parent.GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                renderers[i].gameObject.layer = layer;
            }
        }
    }

    static float ClampAngle(float angle, float min, float max)
    {
        if (angle < -360F)
            angle += 360F;
        if (angle > 360F)
            angle -= 360F;
        return Mathf.Clamp(angle, min, max);
    }

    static string MakeValidFileName(string name)
    {
        var invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

        return System.Text.RegularExpressions.Regex.Replace(name, invalidRegStr, "_");
    }

    static string SaveAsFile(Texture2D texture, string folder, string name, ImageSaveMode whenImageSave)
    {
        string addString = (whenImageSave == ImageSaveMode.Incremental)
            ? DateTime.Now.ToString("MMddHHmmss")
            : string.Empty;
        byte[] bytes = texture.EncodeToPNG();
        var imageFilePath = folder + "/" + MakeValidFileName(string.Format("{0}_{1}.{2}", name, addString, "png"));
        var directoryInfo = (new FileInfo(imageFilePath)).Directory;
        if (directoryInfo != null) directoryInfo.Create();
        File.WriteAllBytes(imageFilePath, bytes);
        //Notice.Log("Image saved to: " + imageFilePath, true);
        return imageFilePath;
    }
    [MenuItem("Tools/See1Studios/SDFShadowTool", false, 0)]
    private static void Init()
    {
        SDFShadowTool window = EditorWindow.GetWindow<SDFShadowTool>("SDF Shadow Tool");
        window.minSize = new Vector2(128, 128);
        window.Show();
    }

    public void AddItemsToMenu(GenericMenu menu)
    {
        GUIContent content = new GUIContent("My Custom Entry");
        menu.AddItem(content, false, MyCallback);
    }
    private void MyCallback()
    {
        Debug.Log("My Callback was called.");
    }
}