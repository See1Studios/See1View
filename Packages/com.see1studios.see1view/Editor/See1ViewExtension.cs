//
// See1View - Unity asset viewer for look dev and additional asset creation
//
// Copyright (C) 2020 See1 Studios - Jongwoo Park
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

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

#if UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
using System.Security.Policy;
#endif
#if URP
using UnityEngine.Rendering.Universal;
#endif
#if HDRP
using UnityEngine.Rendering.HighDefinition;
#endif
using See1Studios.See1View;

namespace See1Studios.See1View
{
    // base model managing
    [Serializable]
    public class ModelGroup
    {
        [Serializable]
        public class AssembleOptions
        {
            public bool ResetTransform = false;
            public bool m_RenderersOnly = false;
            public Vector3 Position = Vector3.zero;
            public Quaternion Rotation = Quaternion.identity;
            public Vector3 Scale = Vector3.one;

            public void ResetCustomOrigin()
            {
                Position = Vector3.zero;
                Rotation = Quaternion.identity;
                Scale = Vector3.one;
            }
        }

        public AnimBool enabled;
        public int SelectedIndex;
        public string m_Name = string.Empty;
        public string m_TargetPath = string.Empty;
        public List<GameObject> m_Sources;
        public AssembleOptions m_Options;

        public bool IsEditingName { get; set; }
        public bool IsExpanded { get; set; }

        public ModelGroup(string name)
        {
            enabled = new AnimBool(true);
            this.m_Name = name;
            if (m_Sources == null)
            {
                m_Sources = new List<GameObject>();
            }

            if (m_Options == null)
            {
                m_Options = new AssembleOptions();
            }
        }
    }

    // manage and assemble multiple models. wip.
    public class ModelAssembler
    {
        internal UnityEditorInternal.ReorderableList rol;
        //internal List<ModelGroup> modelGroupList;

        private static string _nameBuffer = string.Empty;

        //static GUIContent plusIcon = EditorGUIUtility.IconContent("ShurikenPlus");
        //static GUIContent minusIcon = EditorGUIUtility.IconContent("ShurikenMinus");
        //static GUIContent settingsIcon = EditorGUIUtility.IconContent("Inlined TextField Focus");
        private const string MODEL_ROOT_NAME = "Root";

        public static void SetBuiltinNames()
        {
        }

        public void Init(List<ModelGroup> modelGroupList, GenericMenu.MenuFunction dataChangeHandler,
            GenericMenu.MenuFunction2 targetItemHandler, GenericMenu.MenuFunction2 menuItemHandler)
        {
            rol = new UnityEditorInternal.ReorderableList(modelGroupList, typeof(ModelGroup));
            rol.showDefaultBackground = false;
            //Header
            rol.headerHeight = 20;
            rol.drawHeaderCallback = (position) =>
            {
                //var btn20 = position.width * 0.2f;
                var btn25 = position.width * 0.25f;
                //var btn30 = position.width * 0.3f;
                var btn50 = position.width * 0.5f;
                position.width = btn50;
                if (GUI.Button(position, "Reset Names", EditorStyles.miniButton))
                {
                    SetBuiltinNames();
                }

                //position.x += position.width;
                //position.width = btn30;
                //if (GUI.Button(position, "Remove All", EditorStyles.miniButton))
                //{
                //    data.PartDataList.Clear();
                //}
                position.x += position.width;
                position.width = btn25;
                if (GUI.Button(position, "Add Part", EditorStyles.miniButtonLeft))
                {
                    rol.onAddDropdownCallback.Invoke(position, rol);
                }

                position.x += position.width;
                if (GUI.Button(position, "Remove", EditorStyles.miniButtonRight))
                {
                    rol.onRemoveCallback(rol);
                }
            };
            rol.drawFooterCallback = (position) => { };
            //Element
            //reorderableList.elementHeight = EditorGUIUtility.singleLineHeight * 3f;
            rol.elementHeightCallback = (index) =>
            {
                //var height = EditorGUIUtility.singleLineHeight * 5f;

                //    height += 70f;
                return 100;
            };
            rol.drawElementCallback = (position, index, isActive, isFocused) =>
            {
                Rect r = new Rect(position);
                Event evt = Event.current;
                var pData = modelGroupList[index];
                //list 크기가 변할 수 있으므로 인덱스를 일단 계속 갱신해줌 ㅠㅠ
                pData.SelectedIndex = ClampInRange(pData.SelectedIndex, pData.m_Sources.Count);

                //UI Constants
                var listRect = new RectOffset(2, 2, 2, 2).Remove(position);
                const float space = 2f;
                const float lineHeight = 20f;
                const float miniBtnWidth = 20f;
                //var miniButtonwidth = listRect.width * 0.5f;
                const float miniButtonheight = 15f;

                Rect color_area = new Rect(position.x - 15, position.y + 20, 10, 70);
                //float hue = (1.0f / (float)((float)index + 1.0f));
                Color color = isActive ? Color.white : Color.black; // Color.HSVToRGB(hue, 1.0f, 1.0f);
                if (pData.enabled.target)
                    EditorGUI.DrawRect(color_area, color * (isActive ? 0.5f : 0.25f));
                ////1st Row
                EditorGUI.BeginChangeCheck();
                ////Draw Header
                var headerRect = new Rect()
                { x = listRect.x, y = listRect.y, width = listRect.width, height = lineHeight };
                GUI.backgroundColor = pData.enabled.target ? Color.black * 0.5f : Color.white;
                GUI.Box(headerRect, "", "ShurikenModuleTitle");
                GUI.backgroundColor = Color.white;

                ////Toggle Active
                position.x = listRect.x;
                position.width = miniBtnWidth;
                position.height = lineHeight;
                pData.enabled.target = GUI.Toggle(position, pData.enabled.target, "", "OL Toggle");

                ////Default Option
                using (new EditorGUI.DisabledScope(!pData.enabled.target))
                {
                    ////Data Name
                    position.x += position.width;
                    position.width = (listRect.width - miniBtnWidth * 4);

                    if (pData.IsEditingName)
                    {
                        if (Event.current.keyCode == KeyCode.Escape)
                        {
                            pData.IsEditingName = false;
                        }

                        if (Event.current.isMouse && !position.Contains(Event.current.mousePosition))
                        {
                            pData.IsEditingName = false;
                        }

                        using (var inputCheck = new EditorGUI.ChangeCheckScope())
                        {
                            _nameBuffer = EditorGUI.DelayedTextField(position, _nameBuffer);
                            if (inputCheck.changed)
                            {
                                pData.m_Name = _nameBuffer;
                                _nameBuffer = string.Empty;
                                pData.IsEditingName = false;
                            }
                        }
                    }
                    else
                    {
                        GUI.Label(position,
                            string.Format("{0}   {1}/{2}", pData.m_Name, pData.SelectedIndex + 1,
                                pData.m_Sources.Count), Styles.centeredMiniLabel);
                    }

                    position.x += position.width;
                    position.width = miniBtnWidth;
                    if (GUI.Button(position, "Settings", Styles.transButton))
                    {
                        pData.IsEditingName = true;
                        _nameBuffer = pData.m_Name.ToString();
                    }

                    position.x += position.width;
                    position.width = miniBtnWidth;
                    int id = EditorGUIUtility.GetControlID(FocusType.Passive, position);
                    string commandName = evt.commandName;
                    if (commandName == "ObjectSelectorClosed")
                    {
                        var obj = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                        if (obj)
                        {
                            if (pData.m_Sources.All(x => x != obj))
                            {
                                pData.m_Sources.Add(obj);
                                pData.SelectedIndex += 1;
                                pData.SelectedIndex = ClampInRange(pData.SelectedIndex, pData.m_Sources.Count);
                                dataChangeHandler();
                            }
                        }

                        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    }

                    if (GUI.Button(position, "+", Styles.transButton))
                    {
                        EditorGUIUtility.ShowObjectPicker<GameObject>(null, false, "", id);
                    }

                    position.x += position.width;
                    position.width = miniBtnWidth;
                    if (GUI.Button(position, "-", Styles.transButton))
                    {
                        if (pData.m_Sources.Count > 0)
                        {
                            pData.m_Sources.RemoveAt(pData.SelectedIndex);
                            //Refresh Index T_T
                            pData.SelectedIndex = ClampInRange(pData.SelectedIndex, pData.m_Sources.Count);
                            dataChangeHandler();
                        }
                    }

                    ////위에서 인덱스가 바뀔 수 있으므로 여기서 판단함.
                    bool isSourceExist = (pData.m_Sources.Count > 0)
                        ? (pData.m_Sources[pData.SelectedIndex]) != null
                        : false;

                    //2nd Row
                    //position.y += space;
                    position.y += lineHeight;

                    //Index Mod
                    position.x = listRect.x;
                    position.width = miniBtnWidth;
                    position.height = lineHeight;
                    if (GUI.Button(position, "◄", Styles.transButton))
                    {
                        pData.SelectedIndex -= 1;
                        pData.SelectedIndex = ClampInRange(pData.SelectedIndex, pData.m_Sources.Count);
                    }

                    ////Source Field
                    position.x += miniBtnWidth;
                    position.width = (listRect.width - miniBtnWidth * 2);

                    ////Drag and Drop
                    Rect drop_area = position;
                    GUI.Box(drop_area, "", GUI.skin.box);
                    switch (evt.type)
                    {
                        case EventType.DragUpdated:
                        case EventType.DragPerform:
                            if (!r.Contains(evt.mousePosition))
                                return;
                            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                            if (evt.type == EventType.DragPerform)
                            {
                                DragAndDrop.AcceptDrag();
                                foreach (UnityEngine.Object dragged_object in DragAndDrop.objectReferences)
                                {
                                    if (dragged_object is GameObject)
                                    {
                                        pData.m_Sources.Add(dragged_object as GameObject);
                                    }
                                }

                                //Refresh Index T_T
                                pData.SelectedIndex = ClampInRange(pData.SelectedIndex, pData.m_Sources.Count);
                                dataChangeHandler();
                            }

                            break;
                    }

                    string pName = (isSourceExist) ? pData.m_Sources[pData.SelectedIndex].name : "None";
                    var style = new GUIStyle(GUI.skin.label)
                    { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                    if (GUI.Button(position, pName, style))
                    {
                        if (isSourceExist)
                        {
                            Selection.activeObject =
                                EditorUtility.InstanceIDToObject(pData.m_Sources[pData.SelectedIndex]
                                    .GetInstanceID());
                        }
                    }

                    ////Index Mod
                    position.x += position.width;
                    position.width = miniBtnWidth;
                    if (GUI.Button(position, "►", Styles.transButton))
                    {
                        pData.SelectedIndex += 1;
                        pData.SelectedIndex = ClampInRange(pData.SelectedIndex, pData.m_Sources.Count);
                    }

                    position.x = listRect.x;
                    position.width = listRect.width;
                    position.y += space * 4;
                    position.y += miniButtonheight;
                    pData.SelectedIndex =
                        EditorGUI.IntSlider(position, pData.SelectedIndex + 1, 1, pData.m_Sources.Count) -
                        1; // EditorGUI.Vector3Field(position, "", pData.Position);

                    ////Default Option
                    using (new EditorGUI.DisabledScope(!isSourceExist))
                    {
                        position.y += space;
                        position.x = listRect.x;
                        position.y += lineHeight;
                        position.width = listRect.width;
                        position.height = miniButtonheight;
                        if (GUI.Button(position,
                            (string.Format("{0}{1}", "Parent - ",
                                string.IsNullOrEmpty(pData.m_TargetPath) ? MODEL_ROOT_NAME : pData.m_TargetPath)),
                            "MiniPopup"))
                        {
                            targetItemHandler(pData);
                        }

                        //4th Row
                        //position.x = listRect.x;
                        //position.y += space;
                        //position.y += miniButtonheight;
                        //position.width = listRect.width;
                        //pData.IsExpanded = GUI.Toggle(position, pData.IsExpanded, "Options", "MiniButton");
                        //if (pData.IsExpanded)
                        //{
                        position.y += space;
                        position.y += miniButtonheight;
                        position.width = listRect.width / 2;
                        pData.m_Options.ResetTransform = GUI.Toggle(position, pData.m_Options.ResetTransform,
                            "Reset Transform", "MiniButton");
                        position.x += position.width;
                        pData.m_Options.m_RenderersOnly = GUI.Toggle(position, pData.m_Options.m_RenderersOnly,
                            "Renderers Only", "MiniButton");

                        //position.x = listRect.x;
                        //position.width = listRect.width;
                        //position.y += space;
                        //position.y += miniButtonheight;
                        //pData.m.m_Options.Position = EditorGUI.Vector3Field(position, "", pData.m_Options.Position);// EditorGUI.Vector3Field(position, "", pData.Position);
                        //position.y += space;
                        //position.y += miniButtonheight;
                        //pData.m_Options.Rotation.eulerAngles = EditorGUI.Vector3Field(position, "", pData.m_Options.Rotation.eulerAngles);
                        //position.y += space;
                        //position.y += miniButtonheight;
                        //pData.m_Options.Scale = EditorGUI.Vector3Field(position, "", pData.m_Options.Scale);
                        //}
                    }

                    if (evt.type == EventType.Repaint)
                    {
                        if (DragAndDrop.visualMode == DragAndDropVisualMode.Copy && (r.Contains(evt.mousePosition)))
                        {
                            GUI.Box(r, "", GUI.skin.box);
                            EditorGUI.DrawRect(r, new Color(0.5f, 1.0f, 1.0f) * 0.5f);
                        }
                    }

                    if (EditorGUI.EndChangeCheck())
                    {
                        dataChangeHandler();
                    }
                }
            };
            rol.drawElementBackgroundCallback = (rect, index, isActive, isFocused) =>
            {
                if (isActive || isFocused)
                {
                    GUI.Box(rect, "", GUI.skin.box);
                    //EditorGUI.DrawRect(rect, Color.white * 0.5f);
                }
            };
            rol.onChangedCallback = (list) =>
            {
                //Debug.Log("onChangedCallback");
            };
            rol.displayAdd = true;
            rol.displayRemove = true;
            rol.onAddDropdownCallback = (buttonRect, list) =>
            {
                EditorGUI.DrawRect(buttonRect, Color.green);
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Add New Part"), false, menuItemHandler, new ModelGroup("New"));
                menu.AddSeparator("");
                //foreach (var partName in AS_Settings.instance.currentData.PartNames)
                //{
                //    menu.AddItem(new GUIContent(partName), false, menuItemHandler, new ModelGroup(partName));
                //}

                menu.ShowAsContext();
            };
            rol.onRemoveCallback = (list) =>
            {
                if (-1 < list.index && list.index < list.list.Count)
                    modelGroupList.RemoveAt(list.index);
            };
            rol.onCanRemoveCallback = (list) =>
            {
                //Debug.Log("onCanRemoveCallback");
                return true;
            };
            rol.onReorderCallback = (list) =>
            {
                //Debug.Log("onReorderCallback");
                dataChangeHandler();
            };
            //Footer
            //rol.footerHeight = 0;
            //rol.drawFooterCallback = (position) =>
            //{
            //    //EditorGUI.DrawRect(position, Color.blue);
            //};
        }

        public static bool Header(string title, bool isExpanded, bool enabledField)
        {
            var display = isExpanded;
            var enabled = enabledField;
            var rect = GUILayoutUtility.GetRect(16f, 22f, Styles.header);
            GUI.Box(rect, title, Styles.header);

            var toggleRect = new Rect(rect.x + 4f, rect.y + 4f, 13f, 13f);
            var e = Event.current;

            if (e.type == EventType.Repaint)
            {
                Styles.headerCheckbox.Draw(toggleRect, false, false, enabled, false);
            }

            if (e.type == EventType.MouseDown)
            {
                const float kOffset = 2f;
                toggleRect.x -= kOffset;
                toggleRect.y -= kOffset;
                toggleRect.width += kOffset * 2f;
                toggleRect.height += kOffset * 2f;

                if (toggleRect.Contains(e.mousePosition))
                {
                    enabledField = !enabledField;
                    e.Use();
                }
                else if (rect.Contains(e.mousePosition))
                {
                    display = !display;
                    isExpanded = !isExpanded;
                    e.Use();
                }
            }

            return display;
        }

        private static int ClampInRange(int i, int count)
        {
            if (count == 0) return -1;
            else if (i < 0) return count - 1;
            else if (i > count - 1) return 0;
            else return i;
        }

        internal void OnGUI()
        {
            EditorGUILayout.HelpBox("Work in progress", MessageType.Info);
            if (rol != null)
            {
                rol.DoLayoutList();
            }
        }
    }

}