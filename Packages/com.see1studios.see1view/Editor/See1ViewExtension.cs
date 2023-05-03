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
    [Serializable]
    class AssemblerDataManager
    {
        private static AssemblerDataManager _instance;

        public static AssemblerDataManager instance
        {
            get { return (_instance != null) ? _instance : Load(); }
            set { _instance = value; }
        }

        public List<AssemblerData> dataList = new List<AssemblerData>();
        public static TextAsset dataAsset;

        public AssemblerData current
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

        public static string path = "Assets/Editor/See1ViewData.Assembler.json";

        public static readonly string key = string.Format("{0}.{1}", "com.see1studios.see1view.assembler", GetProjectName().ToLower());
        public static UnityEvent onDataChanged = new UnityEvent();
        static bool isAddName;
        static bool isEditName;
        private static string inputStr;
        public static bool _isDirty;

        public bool Add(string name)
        {
            bool canAdd = CheckName(name);
            while (!canAdd)
            {
                name += "_1";
                canAdd = CheckName(name);
            }

            AssemblerData data = new AssemblerData(name);
            dataList.Add(data);
            dataIndex = dataList.Count - 1;
            return canAdd;
        }

        public bool RemoveCurrent()
        {
            dataList.Remove(dataList[dataIndex]);
            dataIndex -= 1;
            return true;
        }

        public bool Remove(string name)
        {
            dataList.Remove(dataList.FirstOrDefault(x => x.name == name));
            dataIndex -= 1;
            return true;
        }

        public bool Remove(AssemblerData data)
        {
            if (dataList.Contains(data))
            {
                dataList.Remove(data);
                Mathf.Clamp(dataIndex -= 1, 0, dataList.Count);
                return true;
            }

            return false;
        }

        private static AssemblerDataManager Load()
        {
            _instance = new AssemblerDataManager();
            dataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            string data = string.Empty;
            if (dataAsset)
            {
                data = dataAsset.text;
                JsonUtility.FromJsonOverwrite(data, _instance);
                _isDirty = false;
            }
            else
            {
                _instance.Add("Default");
                SetDirty();
            }

            //var json = EditorPrefs.GetString(key);
            //JsonUtility.FromJsonOverwrite(json, instance);
            //if (instance.dataList.Count == 0)
            //{
            //    instance.dataList.Add(new Data("Data"));
            //    Debug.Log("There is no data. Default Data Created.");
            //    Save();
            //}
            return _instance;
        }

        public static void Save()
        {
            var json = JsonUtility.ToJson(instance, true);
            DirectoryInfo di = new DirectoryInfo(Application.dataPath.Replace("Assets", "") + Path.GetDirectoryName(path));
            if (!di.Exists) di.Create();
            AssetDatabase.Refresh();
            File.WriteAllText(path, json);
            AssetDatabase.Refresh();
            dataAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            EditorPrefs.SetString(key, json);
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
            AssemblerData data = current.Clone() as AssemblerData;
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
                if (isAddName || isEditName)
                {
                    GUI.SetNextControlName("input");
                    inputStr = EditorGUILayout.TextField(inputStr);
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
                    instance.dataIndex = (int)EditorGUILayout.Popup(instance.dataIndex, dataNames, EditorStyles.toolbarPopup);
                }

                if (GUILayout.Button("+", EditorStyles.toolbarButton))
                {
                    isAddName = true;
                    inputStr = "New";
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    EditorGUI.FocusTextInControl("input");
                }

                using (new EditorGUI.DisabledGroupScope(instance.dataList.Count == 1))
                {
                    if (GUILayout.Button("-", EditorStyles.toolbarButton))
                    {
                        if (EditorUtility.DisplayDialog("Confirm", string.Format("{0}{1}{2}", "Delete ", instance.current.name, "?"), "Ok", "Cancel"))
                        {
                            instance.RemoveCurrent();
                        }
                    }
                }

                if (GUILayout.Button("=", EditorStyles.toolbarButton))
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
                        Notice.Log(string.Format("Assembler Data Chaneged to {0}", instance.current.name));
                    }
                }
            }
        }
    }


    [Serializable]
    public class AssemblerData : ICloneable
    {
        public string name;
        public bool reframeOnChange;
        public bool resetAnimationOnChange;
        public GameObject Preset;
        public List<ModelPart> PartDataList = new List<ModelPart>();
        public List<string> PartNames = new List<string>();

        public void OnEnable()
        {
            if (PartDataList == null)
            {
                PartDataList = new List<ModelPart>();
            }
            if (PartNames == null)
            {
                PartNames = new List<string>();
            }
        }
        internal void SetBuiltinNames()
        {
            PartNames.Clear();
            PartNames.Add("Face");
            PartNames.Add("Hair");
            PartNames.Add("Head");
            PartNames.Add("Body");
            PartNames.Add("Upper");
            PartNames.Add("Lower");
            PartNames.Add("Weapon_L");
            PartNames.Add("Weapon_R");
            PartNames.Add("Shoulder");
            PartNames.Add("Hand");
            PartNames.Add("Leg");
            PartNames.Add("Foot");
            PartNames.Add("Acc");
        }

        public AssemblerData(string name)
        {
            this.name = name;
        }

        public object Clone()
        {
            return this.MemberwiseClone();
        }
    }
    // base model managing
    [Serializable]
    public class ModelPart
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

        public ModelPart(string name)
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
        internal List<ModelPart> modelGroupList = new List<ModelPart>();

        private static string _nameBuffer = string.Empty;

        //static GUIContent plusIcon = EditorGUIUtility.IconContent("ShurikenPlus");
        //static GUIContent minusIcon = EditorGUIUtility.IconContent("ShurikenMinus");
        //static GUIContent settingsIcon = EditorGUIUtility.IconContent("Inlined TextField Focus");
        private const string MODEL_ROOT_NAME = "Root";

        //public void Init(GenericMenu.MenuFunction dataChangeHandler,
        //    GenericMenu.MenuFunction2 targetItemHandler, GenericMenu.MenuFunction2 menuItemHandler)
        public void Init()
        {
            rol = new UnityEditorInternal.ReorderableList(modelGroupList, typeof(ModelPart));
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
                    AssemblerDataManager.instance.current.SetBuiltinNames();
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
                menu.AddItem(new GUIContent("Add New Part"), false, menuItemHandler, new ModelPart("New"));
                menu.AddSeparator("");
                foreach (var partName in AssemblerDataManager.instance.current.PartNames)
                {
                    menu.AddItem(new GUIContent(partName), false, menuItemHandler, new ModelPart(partName));
                }

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

        private void menuItemHandler(object target)
        {
            ModelPart pData = target as ModelPart;
            if (pData != null)
            {
                AssemblerDataManager.instance.current.PartDataList.Add(pData);
                AssemblerDataManager.SetDirty();
            }
        }

        private void targetItemHandler(ModelPart pData)
        {
        }

        private void dataChangeHandler()
        {
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