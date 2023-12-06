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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
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
#endif
#if URP
using UnityEngine.Rendering.Universal;
#endif
#if HDRP
using UnityEngine.Rendering.HighDefinition;
#endif

namespace See1Studios.See1View
{
    #region Enum & Flags //-----------------------------------------------------------------------------------------------------------------------------------------------

    [Flags]
    public enum GizmoMode
    {
        //None = (1<<0),
        Info = (1 << 1),
        Light = (1 << 2),
        Bound = (1 << 3),
        Bone = (1 << 4)
    }

    public enum ModelCreateMode
    {
        Default,
        Preview,
        Custom
    }

    public enum SidePanelMode
    {
        View,
        Model,
        Animation,
        Misc
    }

    public enum ClearFlags
    {
        Color,
        Sky
    }

    public enum ImageSaveMode
    {
        Overwrite,
        Incremental
    }

    public enum ViewMode
    {
        None,
        Depth,
        Normal
    }

    public enum RenderPipelineMode
    {
        BuiltIn,
        Universal,
        HighDefinition,
    }

    #endregion

    #region Classes //-----------------------------------------------------------------------------------------------------------------------------------------------

    [InitializeOnLoad]
    public static class Initializer
    {
        [System.Serializable]
        class ConfigData
        {
            public string title;
            public string logoTexStr;
            public string description;
            public string help;
            public string customLoader;
        }

        public const string MENU_PATH = "Tools/See1Studios/See1View/Open See1View";
        public const int MENU_PRIORITY = 10000;

        public static string title = "See1View";
        public static string logoTexStr = "";
        public static string description = "Copyright (c) See1Studios.\nsee1studios@gmail.com";
        public static string help = "No Help Document";
        public static Texture2D logoTexture = Texture2D.grayTexture;
        static string _customLoaderTypeName;

        static Type[] _customLoaderTypes;

        static Initializer()
        {
            CollectCustomLoaders();
            InitializeConfig();
        }
        static void InitializeConfig()
        {
            string cfgPath = PathHelper.ScriptPath.Replace(".cs", ".cfg");
            if (File.Exists(cfgPath))
            {
                string json = File.ReadAllText(cfgPath);
                ConfigData data = JsonUtility.FromJson<ConfigData>(json);
                title = data.title;
                logoTexStr = data.logoTexStr;
                description = data.description;
                _customLoaderTypeName = data.customLoader;
                logoTexture = ConvertBase64ToTexture(data.logoTexStr);
                help = data.help;
                Debug.Log(_customLoaderTypeName);
            }
        }

        public static void CollectCustomLoaders()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            Type baseClassType = typeof(CustomLoader);
            _customLoaderTypes = GetDerivedTypes(assembly, baseClassType);
            foreach (var customLoader in _customLoaderTypes)
            {
                Debug.Log(customLoader.Name);
            }
        }

        public static Type[] GetDerivedTypes(Assembly assembly, Type baseType)
        {
            return assembly.GetTypes()
                .Where(t => t != baseType && baseType.IsAssignableFrom(t))
                .ToArray();
        }

        public static CustomLoader CreateCustomLoader(See1View see1View)
        {
            Type type = _customLoaderTypes.Where(x => x.Name == _customLoaderTypeName).FirstOrDefault();
            if (type == null && _customLoaderTypes.Length > 0) type = _customLoaderTypes[0];
            if (type != null)
            {
                ConstructorInfo constructor = type.GetConstructors().FirstOrDefault();
                if (constructor != null)
                {
                    ParameterInfo[] parameters = constructor.GetParameters();

                    object[] parameterValues = new object[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        parameterValues[i] = GetDefaultValue(parameters[i].ParameterType);
                        if (parameters[i].ParameterType == typeof(See1View))
                        {
                            parameterValues[i] = see1View;
                        }
                    }

                    object instance = Activator.CreateInstance(type, parameterValues);
                    return instance as CustomLoader;
                }
            }
            return null;
        }

        private static object GetDefaultValue(Type type)
        {
            if (type.IsValueType)
            {
                return Activator.CreateInstance(type);
            }
            else { return null; }
        }

        public static Texture2D ConvertBase64ToTexture(string base64)
        {
            byte[] bytes = System.Convert.FromBase64String(base64);
            var tex = new Texture2D(1, 1);
            tex.LoadImage(bytes);
            return tex;
        }
    }

    public static class PathHelper
    {
        public static string ScriptPath
        {
            get
            {
                var g = AssetDatabase.FindAssets($"t:Script {nameof(See1View)}");
                string absolute = Path.GetFullPath(AssetDatabase.GUIDToAssetPath(g[0]));
                return absolute;
            }
        }
    }

    public class EditorCoroutine
    {
        public class EditorWaitForSeconds
        {
            public double WaitTime { get; set; }

            public EditorWaitForSeconds(float time)
            {
                WaitTime = time;
            }
        }

        private struct YieldProcessor
        {
            enum DataType : byte
            {
                None = 0,
                WaitForSeconds = 1,
                EditorCoroutine = 2,
                AsyncOP = 3,
            }

            struct ProcessorData
            {
                public DataType type;
                public double targetTime;
                public object current;
            }

            ProcessorData data;

            public void Set(object yield)
            {
                if (yield == data.current)
                    return;

                var type = yield.GetType();
                var dataType = DataType.None;
                double targetTime = -1;
                if (type == typeof(EditorWaitForSeconds))
                {
                    targetTime = EditorApplication.timeSinceStartup + (yield as EditorWaitForSeconds).WaitTime;
                    dataType = DataType.WaitForSeconds;
                }
                else if (type == typeof(EditorCoroutine))
                {
                    dataType = DataType.EditorCoroutine;
                }
                else if (type == typeof(AsyncOperation))
                {
                    dataType = DataType.AsyncOP;
                }

                data = new ProcessorData { current = yield, targetTime = targetTime, type = dataType };
            }

            public bool MoveNext(IEnumerator enumerator)
            {
                bool advance = false;
                switch (data.type)
                {
                    case DataType.WaitForSeconds:
                        advance = data.targetTime <= EditorApplication.timeSinceStartup;
                        break;
                    case DataType.EditorCoroutine:
                        advance = (data.current as EditorCoroutine).m_IsDone;
                        break;
                    case DataType.AsyncOP:
                        advance = (data.current as AsyncOperation).isDone;
                        break;
                    default:
                        advance = data.current ==
                                  enumerator
                                      .Current; //a IEnumerator or a plain object was passed to the implementation
                        break;
                }

                if (advance)
                {
                    data = default(ProcessorData);
                    return enumerator.MoveNext();
                }

                return true;
            }
        }

        internal WeakReference m_Owner;
        IEnumerator m_Routine;
        YieldProcessor m_Processor;

        bool m_IsDone;

        internal EditorCoroutine(IEnumerator routine)
        {
            m_Owner = null;
            m_Routine = routine;
            EditorApplication.update += MoveNext;
        }

        internal EditorCoroutine(IEnumerator routine, object owner)
        {
            m_Processor = new YieldProcessor();
            m_Owner = new WeakReference(owner);
            m_Routine = routine;
            EditorApplication.update += MoveNext;
        }

        internal void MoveNext()
        {
            if (m_Owner != null && !m_Owner.IsAlive)
            {
                EditorApplication.update -= MoveNext;
                return;
            }

            bool done = ProcessIEnumeratorRecursive(m_Routine);
            m_IsDone = !done;

            if (m_IsDone)
                EditorApplication.update -= MoveNext;
        }

        static Stack<IEnumerator> kIEnumeratorProcessingStack = new Stack<IEnumerator>(32);

        private bool ProcessIEnumeratorRecursive(IEnumerator enumerator)
        {
            var root = enumerator;
            while (enumerator.Current as IEnumerator != null)
            {
                kIEnumeratorProcessingStack.Push(enumerator);
                enumerator = enumerator.Current as IEnumerator;
            }

            //process leaf
            m_Processor.Set(enumerator.Current);
            var result = m_Processor.MoveNext(enumerator);

            while (kIEnumeratorProcessingStack.Count > 1)
            {
                if (!result)
                {
                    result = kIEnumeratorProcessingStack.Pop().MoveNext();
                }
                else
                    kIEnumeratorProcessingStack.Clear();
            }

            if (kIEnumeratorProcessingStack.Count > 0 && !result && root == kIEnumeratorProcessingStack.Pop())
            {
                result = root.MoveNext();
            }

            return result;
        }

        internal void Stop()
        {
            m_Owner = null;
            m_Routine = null;
            EditorApplication.update -= MoveNext;
        }
    }
    // force execute monobehavior
    public class RunInEditHelper
    {
        static bool isUpdating;
        static void Add()
        {

            isUpdating = true;
            EditorApplication.update += EditorUpdate;

        }
        static void Remove()
        {
            isUpdating = false;
            EditorApplication.update -= EditorUpdate;
        }


        static void UpdateChildren(GameObject rootObject)
        {

            foreach (var behaviour in rootObject.GetComponents<MonoBehaviour>())
            {
#if UNITY_2020_OR_NEWER
                behaviour.StartRunInEditMode();
#endif
            }

        }
        static void StopChildren(GameObject rootObject)
        {
            foreach (var behaviour in rootObject.GetComponents<MonoBehaviour>())
            {
#if UNITY_2020_OR_NEWER
                behaviour.StopRunInEditMode();
#endif
            }
        }

        static void EditorUpdate()
        {
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }

    public class RunInEditHelper2
    {
        public class RunInEdit
        {
            public bool awake = true;
            public bool onEnable = true;
            public bool start = true;
            public bool update = true;
            public bool lateUpdate = true;
            public bool onDisable = true;
            public bool onDestroy = true;
            public MonoBehaviour behaviour;

            public RunInEdit(MonoBehaviour behaviour)
            {
                this.behaviour = behaviour;
            }
        }

        public static List<RunInEdit> runInEditList = new List<RunInEdit>();
        static bool isInitialised;
        static bool playStateChanged;
        public static bool isActivated;

        public static void Toggle()
        {
            foreach (var rie in runInEditList)
            {
                rie.behaviour?.CancelInvoke();
            }
            isActivated = !isActivated;
            playStateChanged = true;
            Start();
            Stop();
        }

        public static void Start()
        {
            foreach (var rie in runInEditList)
            {

                if (isActivated && playStateChanged)
                {
                    if (rie.awake) Invoke(rie, "Awake");
                    if (rie.onEnable) Invoke(rie, "OnEnable");
                    if (rie.start) Invoke(rie, "Start");
                    playStateChanged = false;
                    EditorApplication.update += Update;
                }
            }
        }

        public static void Stop()
        {
            foreach (var rie in runInEditList)
            {
                if (!isActivated && playStateChanged)
                {
                    if (rie.onDisable) Invoke(rie, "OnDisable");
                    if (rie.onDestroy) Invoke(rie, "OnDestroy");
                    playStateChanged = false;
                    EditorApplication.update -= Update;
                }
            }
        }

        public static void Update()
        {
            foreach (var rie in runInEditList)
            {
                if (rie.behaviour)
                {
                    if (isActivated && !playStateChanged)
                    {
                        if (rie.update) Invoke(rie, "Update");
                        if (rie.lateUpdate) Invoke(rie, "LateUpdate");
                        playStateChanged = false;
                        EditorUtility.SetDirty(rie.behaviour); // For Scene Update
                    }
                }
            }
            //if (runInEditList.Count > 0)
            //{
            //    SceneView.RepaintAll();
            //}
        }

        public static void Add(MonoBehaviour behavior)
        {
            if (behavior != null) runInEditList.Add(new RunInEdit(behavior));
        }

        public static void Remove(MonoBehaviour behavior)
        {
            RunInEdit rie = runInEditList.Where(x => x.behaviour == behavior).FirstOrDefault();
            if (rie != null) runInEditList.Remove(rie);
        }

        public static void Clean()
        {

            Stop();
            runInEditList.Clear();
        }

        static void Invoke(RunInEdit rie, string eventName, bool cancel = false)
        {
            if (cancel) rie.behaviour?.CancelInvoke();
            MethodInfo methodInfo = rie.behaviour?.GetType().GetMethod(eventName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Instance);
            if (methodInfo != null)
            {
                //methodInfo.Invoke(rie.behaviour, null);
                rie.behaviour?.Invoke(eventName, 0);

            }
        }
    }

    //saved parameters from URP GUI
    class SavedParameter<T>
where T : IEquatable<T>
    {
        internal delegate void SetParameter(string key, T value);
        internal delegate T GetParameter(string key, T defaultValue);

        readonly string m_Key;
        bool m_Loaded;
        T m_Value;

        readonly SetParameter m_Setter;
        readonly GetParameter m_Getter;

        public SavedParameter(string key, T value, GetParameter getter, SetParameter setter)
        {
            Assert.IsNotNull(setter);
            Assert.IsNotNull(getter);

            m_Key = key;
            m_Loaded = false;
            m_Value = value;
            m_Setter = setter;
            m_Getter = getter;
        }

        void Load()
        {
            if (m_Loaded)
                return;

            m_Loaded = true;
            m_Value = m_Getter(m_Key, m_Value);
        }

        public T value
        {
            get
            {
                Load();
                return m_Value;
            }
            set
            {
                Load();

                if (m_Value.Equals(value))
                    return;

                m_Value = value;
                m_Setter(m_Key, value);
            }
        }
    }

    sealed class SavedBool : SavedParameter<bool>
    {
        public SavedBool(string key, bool value)
            : base(key, value, EditorPrefs.GetBool, EditorPrefs.SetBool) { }
    }

    sealed class SavedInt : SavedParameter<int>
    {
        public SavedInt(string key, int value)
            : base(key, value, EditorPrefs.GetInt, EditorPrefs.SetInt) { }
    }

    sealed class SavedFloat : SavedParameter<float>
    {
        public SavedFloat(string key, float value)
            : base(key, value, EditorPrefs.GetFloat, EditorPrefs.SetFloat) { }
    }

    sealed class SavedString : SavedParameter<string>
    {
        public SavedString(string key, string value)
            : base(key, value, EditorPrefs.GetString, EditorPrefs.SetString) { }
    }

    public static class EditorCoroutineUtility
    {
        public static EditorCoroutine StartCoroutine(IEnumerator routine, object owner)
        {
            return new EditorCoroutine(routine, owner);
        }

        public static EditorCoroutine StartCoroutineOwnerless(IEnumerator routine)
        {
            return new EditorCoroutine(routine);
        }

        public static void StopCoroutine(EditorCoroutine coroutine)
        {
            if (coroutine == null)
            {
                return;
            }

            coroutine.Stop();
        }
    }

    public static class EditorWindowControl
    {
        public enum SelectWindowType
        {
            Inspector,
            ProjectBrowser,
            Game,
            Console,
            Hierarchy,
            Scene
        };

        public static Type GetBuiltinWindowType(SelectWindowType swt)
        {
            System.Type unityEditorWindowType = null;
            switch (swt)
            {
                case SelectWindowType.Inspector:
                    unityEditorWindowType =
                        typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.InspectorWindow");
                    break;
                case SelectWindowType.ProjectBrowser:
                    unityEditorWindowType =
                        typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ProjectBrowser");
                    break;
                case SelectWindowType.Game:
                    unityEditorWindowType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
                    break;
                case SelectWindowType.Console:
                    unityEditorWindowType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ConsoleView");
                    break;
                case SelectWindowType.Hierarchy:
                    unityEditorWindowType =
                        typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
                    break;
                case SelectWindowType.Scene:
                    unityEditorWindowType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.SceneView");
                    break;
            }

            return unityEditorWindowType;
        }
    }



    // .Ini file Parser Author: Tristan 'Kennyist' Cunningham - www.tristanjc.com License: Creative commons ShareAlike 3.0 - https://creativecommons.org/licenses/by-sa/3.0/
    public class INIParser
    {

        private ArrayList keys = new ArrayList();
        private ArrayList vals = new ArrayList();
        private ArrayList comments = new ArrayList();

        public INIParser() { }

        public INIParser(string file)
        {
            Load(file);
        }

        public bool DoesExist(string file)
        {
            return File.Exists(Application.dataPath + "/" + file + ".ini") ? true : false;
        }

        public void Set(string key, string val)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == key)
                {
                    vals[i] = val;
                    return;
                }
            }

            keys.Add(key);
            vals.Add(val);
            comments.Add("");
        }

        public void Set(string key, string val, string comment)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == key)
                {
                    vals[i] = val;
                    comments[i] = comment;
                    return;
                }
            }

            keys.Add(key);
            vals.Add(val);
            comments.Add(comment);
        }

        public string Get(string key)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].Equals(key))
                {
                    return vals[i].ToString();
                }
            }
            return "";
        }

        public string[] GetLine(string key)
        {
            string[] list = new string[2];

            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].Equals(key))
                {
                    list[0] = keys[i].ToString();
                    list[1] = vals[i].ToString();
                    list[2] = comments[i].ToString();
                    return list;
                }
            }

            return list;
        }

        public void Remove(string key)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == key)
                {
                    keys.RemoveAt(i);
                    vals.RemoveAt(i);
                    comments.RemoveAt(i);
                    return;
                }
            }
            Debug.LogWarning("Key not found");
        }

        public void Save(string file)
        {
            StreamWriter wr = new StreamWriter(Application.dataPath + "/" + file + ".ini");

            for (int i = 0; i < keys.Count; i++)
            {
                if (comments[i].Equals(""))
                {
                    wr.WriteLine(keys[i] + "=" + vals[i]);
                }
                else
                {
                    wr.WriteLine(keys[i] + "=" + vals[i] + " //" + comments[i]);
                }
            }

            wr.Close();

            Debug.Log(file + ".ini Saved");
        }

        public void Load(string file)
        {
            keys = new ArrayList();
            vals = new ArrayList();
            comments = new ArrayList();

            string line = "";
            int offset = 0, comment = 0;

            try
            {
                using (StreamReader sr = new StreamReader(file))
                {
                    while ((line = sr.ReadLine()) != null)
                    {
                        offset = line.IndexOf("=");
                        comment = line.IndexOf("//");
                        if (offset > 0)
                        {
                            if (comment != -1)
                            {
                                Set(line.Substring(0, offset), line.Substring(offset + 1, (comment - (offset + 1))), line.Substring(comment + 1));
                            }
                            else
                            {
                                Set(line.Substring(0, offset), line.Substring(offset + 1));
                            }
                        }
                    }
                    sr.Close();
                    Debug.Log(file + " Loaded");
                }
            }
            catch (IOException e)
            {
                Debug.Log("Error opening " + file + ".ini");
                Debug.LogWarning(e);
            }
        }

        public int Count()
        {
            return keys.Count;
        }
    }

    public class Shaders
    {
        private static Shader _heightFog;

        public static Shader heightFog
        {
            get
            {
                if (_heightFog == null)
                {
                    _heightFog = ShaderUtil.CreateShaderAsset(
                        "Shader \"See1View/HeightFog\"\n{\nProperties\n{\n_Height (\"Height\", Float) = 2\n_Ground (\"Ground\", Float) = 0\n_Color (\"Color\", Color) = (0, 0, 0, 0)\n}\n\nSubShader\n{\nTags { \"RenderType\" = \"Opaque\" }\nLOD 100\n\nPass\n{\nColorMask RGB\nBlend SrcAlpha  OneMinusSrcAlpha\n//Blend Zero SrcColor\nCGPROGRAM\n\n#pragma vertex vert\n#pragma fragment frag\n#include \"UnityCG.cginc\"\n\nstruct appdata_t\n{\nfloat4 vertex: POSITION;\n};\n\nstruct v2f\n{\nfloat4 vertex: SV_POSITION;\nfloat3 worldPos: TEXCOORD0;\n};\n\nfixed _Height;\nfixed _Ground;\nfixed4 _Color;\n\n// remap value to 0-1 range\nfloat remap(float value, float minSource, float maxSource)\n{\nreturn(value - minSource) / (maxSource - minSource);\n}\n\nv2f vert(appdata_t v)\n{\nv2f o;\no.vertex = UnityObjectToClipPos(v.vertex);\no.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;\nreturn o;\n}\n\nfixed4 frag(v2f i): COLOR\n{\nfixed4 c = fixed4(0, 0, 0, 0);\nfloat bottom = _Ground;\nfloat top = _Ground + _Height;\nfloat v = remap(clamp(i.worldPos.y, bottom, top), bottom, top);\nfixed4 t = fixed4(0,0,0,0);\nc = lerp(_Color, t, v);\nreturn c;\n}\nENDCG\n\n}\n}\n}");
                }

                return _heightFog;
            }
        }

        private static Shader _planarShadow;

        public static Shader planarShadow
        {
            get
            {
                if (_planarShadow == null)
                {
                    _planarShadow = ShaderUtil.CreateShaderAsset(
                        "Shader \"See1View/PlanarShadow\" \n{\n\nProperties {\n_ShadowColor (\"Shadow Color\", Color) = (0,0,0,1)\n_PlaneHeight (\"Plane Height\", Float) = 0\n}\n\nSubShader {\nTags {\"Queue\"=\"Transparent\" \"IgnoreProjector\"=\"True\" \"RenderType\"=\"Transparent\"}\n\n// shadow color\nPass {   \n\nZWrite On\nZTest LEqual \nBlend SrcAlpha  OneMinusSrcAlpha\n\nStencil {\nRef 0\nComp Equal\nPass IncrWrap\nZFail Keep\n}\n\nCGPROGRAM\n#include \"UnityCG.cginc\"\n\n// User-specified uniforms\nuniform float4 _ShadowColor;\nuniform float _PlaneHeight = 0;\n\nstruct vsOut\n{\nfloat4 pos: SV_POSITION;\n};\n\nvsOut vertPlanarShadow( appdata_base v)\n{\nvsOut o;\n                     \nfloat4 vPosWorld = mul( unity_ObjectToWorld, v.vertex);\nfloat4 lightDirection = -normalize(_WorldSpaceLightPos0); \n\nfloat opposite = vPosWorld.y - _PlaneHeight;\nfloat cosTheta = -lightDirection.y;// = lightDirection dot (0,-1,0)\nfloat hypotenuse = opposite / cosTheta;\nfloat3 vPos = vPosWorld.xyz + ( lightDirection * hypotenuse );\n\no.pos = mul (UNITY_MATRIX_VP, float4(vPos.x, _PlaneHeight, vPos.z ,1));  \n\nreturn o;\n}\n\nfloat4 fragPlanarShadow( vsOut i)\n{\nreturn _ShadowColor;\n}\n#pragma vertex vert\n#pragma fragment frag\n\nvsOut vert( appdata_base v)\n{\nreturn vertPlanarShadow(v);\n}\n\n\nfixed4 frag( vsOut i) : COLOR\n{\nreturn fragPlanarShadow(i);\n}\n\nENDCG\n\n}\n}\n}\n");
                }

                return _planarShadow;
            }
        }

        private static Shader _wireFrame;

        public static Shader wireFrame
        {
            get
            {
                if (_wireFrame == null)
                {
                    _wireFrame = ShaderUtil.CreateShaderAsset(
                        "Shader \"See1View/Wireframe\"\n{\nProperties\n{\n_LineColor (\"LineColor\", Color) = (1,1,1,1)\n_FillColor (\"FillColor\", Color) = (0,0,0,0)\n_WireThickness (\"Wire Thickness\", RANGE(0, 800)) = 100\n[MaterialToggle] UseDiscard(\"Discard Fill\", Float) = 1\n[MaterialToggle] UVMode(\"UV Mode\", Float) = 0\n }\n\nSubShader\n{\nTags { \"RenderType\"=\"Opaque\" }\n\n\nPass\n{\nBlend SrcAlpha  OneMinusSrcAlpha\n\nCGPROGRAM\n#pragma vertex vert\n#pragma geometry geom\n#pragma fragment frag\n#pragma multi_compile _ USEDISCARD_ON\n#pragma multi_compile _ UVMODE_ON\n#include \"UnityCG.cginc\"\n\nfloat _WireThickness;\n\nstruct appdata\n{\nfloat4 vertex : POSITION;\n};\n\nstruct v2g\n{\nfloat4 projectionSpaceVertex : SV_POSITION;\nfloat4 worldSpacePosition : TEXCOORD1;\n};\n\nstruct g2f\n{\nfloat4 projectionSpaceVertex : SV_POSITION;\nfloat4 worldSpacePosition : TEXCOORD0;\nfloat4 dist : TEXCOORD1;\n};\n\n\nv2g vert (appdata v)\n{\nv2g o;\n//UNITY_SETUP_INSTANCE_ID(v);\n//UNITY_INITIALIZE_OUTPUT(v2g, o);\n#ifdef UV_ON\nv.vertex = float4(v.uv.xy, 0.0, 1.0);\no.projectionSpaceVertex = mul(UNITY_MATRIX_P, v.vertex);\no.worldSpacePosition = mul(UNITY_MATRIX_P, v.vertex);\n//o.vertex = UnityObjectToClipPos(v.vertex);\n#else\no.projectionSpaceVertex = UnityObjectToClipPos(v.vertex);\no.worldSpacePosition = mul(unity_ObjectToWorld, v.vertex);\n#endif\nreturn o;\n}\n\n[maxvertexcount(3)]\nvoid geom(triangle v2g i[3], inout TriangleStream<g2f> triangleStream)\n{\nfloat2 p0 = i[0].projectionSpaceVertex.xy / i[0].projectionSpaceVertex.w;\nfloat2 p1 = i[1].projectionSpaceVertex.xy / i[1].projectionSpaceVertex.w;\nfloat2 p2 = i[2].projectionSpaceVertex.xy / i[2].projectionSpaceVertex.w;\n\nfloat2 edge0 = p2 - p1;\nfloat2 edge1 = p2 - p0;\nfloat2 edge2 = p1 - p0;\n\n// To find the distance to the opposite edge, we take the\n// formula for finding the area of a triangle Area = Base/2 * Height, \n// and solve for the Height = (Area * 2)/Base.\n// We can get the area of a triangle by taking its cross product\n// divided by 2.  However we can avoid dividing our area/base by 2\n// since our cross product will already be double our area.\nfloat area = abs(edge1.x * edge2.y - edge1.y * edge2.x);\nfloat wireThickness = 800 - _WireThickness;\n\ng2f o;\no.worldSpacePosition = i[0].worldSpacePosition;\no.projectionSpaceVertex = i[0].projectionSpaceVertex;\no.dist.xyz = float3( (area / length(edge0)), 0.0, 0.0) * o.projectionSpaceVertex.w * wireThickness;\no.dist.w = 1.0 / o.projectionSpaceVertex.w;\ntriangleStream.Append(o);\n\no.worldSpacePosition = i[1].worldSpacePosition;\no.projectionSpaceVertex = i[1].projectionSpaceVertex;\no.dist.xyz = float3(0.0, (area / length(edge1)), 0.0) * o.projectionSpaceVertex.w * wireThickness;\no.dist.w = 1.0 / o.projectionSpaceVertex.w;\ntriangleStream.Append(o);\n\no.worldSpacePosition = i[2].worldSpacePosition;\no.projectionSpaceVertex = i[2].projectionSpaceVertex;\no.dist.xyz = float3(0.0, 0.0, (area / length(edge2))) * o.projectionSpaceVertex.w * wireThickness;\no.dist.w = 1.0 / o.projectionSpaceVertex.w;\ntriangleStream.Append(o);\n}\n\nuniform fixed4 _LineColor;\nuniform fixed4 _FillColor;\n\nfixed4 frag (g2f i) : SV_Target\n{\nfloat minDistanceToEdge = min(i.dist[0], min(i.dist[1], i.dist[2])) * i.dist[3];\n\n// Early out if we know we are not on a line segment.\nif(minDistanceToEdge > 0.9)\n{\n#ifdef USEDISCARD_ON\ndiscard;\n#else\nreturn _FillColor;\n#endif\n}\n\nreturn _LineColor;\n}\nENDCG\n}\n}\n}");
                }

                return _wireFrame;
            }
        }

        private static Shader _depth;

        public static Shader depth
        {
            get
            {
                if (_depth == null)
                {
                    _depth = ShaderUtil.CreateShaderAsset(
                        "Shader \"See1View/Depth\"\n{\nProperties\n{\n_MainTex (\"Texture\", 2D) = \"white\" { }\n_Seperate (\"Seperate\", range(0, 1)) = 0.5\n}\nSubShader\n{\n// No culling or depth\nCull Off ZWrite Off ZTest Always\n\nPass\n{\nCGPROGRAM\n\n#pragma vertex vert\n#pragma fragment frag\n\n#include \"UnityCG.cginc\"\n			\nsampler2D _MainTex;\nsampler2D _CameraDepthTexture;\nfloat4 _CameraDepthTexture_TexelSize;\nhalf _Seperate;\n\nstruct appdata\n{\nfloat4 vertex: POSITION;\nfloat2 uv: TEXCOORD0;\n};\n\nstruct v2f\n{\nfloat2 uv: TEXCOORD0;\nfloat4 vertex: SV_POSITION;\n};\n\nv2f vert(appdata v)\n{\nv2f o;\no.vertex = UnityObjectToClipPos(v.vertex);\no.uv = v.uv;\nreturn o;\n}\n\n\nfixed4 frag(v2f i): SV_Target\n{\nfloat4 col = float4(1, 0, 0, 1);\nif (i.vertex.x > _CameraDepthTexture_TexelSize.z / (1 / _Seperate))\n{\nfloat depth = tex2D(_CameraDepthTexture, i.uv).r;\ncol = float4(depth, depth, depth, 1);\n}\nelse\n{\ncol = tex2D(_MainTex, i.uv);\n}\nreturn col;\n}\nENDCG\n\n}\n}\n}\n");
                }

                return _depth;
            }
        }

        private static Shader _depthNormal;

        public static Shader depthNormal
        {
            get
            {
                if (_depthNormal == null)
                {
                    _depthNormal = ShaderUtil.CreateShaderAsset(
                        "Shader \"See1View/DepthNormal\"\n{\nProperties\n{\n_MainTex (\"Texture\", 2D) = \"white\" { }\n_Seperate (\"Seperate\", range(0, 1)) = 0.5\n}\nSubShader\n{\n// No culling or depth\nCull Off ZWrite Off ZTest Always\n\nPass\n{\nCGPROGRAM\n\n#pragma vertex vert\n#pragma fragment frag\n\n#include \"UnityCG.cginc\"\n\nsampler2D _MainTex;\nsampler2D _CameraDepthNormalsTexture;\nfloat4 _CameraDepthNormalsTexture_TexelSize;\nhalf _Seperate;\n\nstruct appdata\n{\nfloat4 vertex: POSITION;\nfloat2 uv: TEXCOORD0;\n};\n\nstruct v2f\n{\nfloat2 uv: TEXCOORD0;\nfloat4 vertex: SV_POSITION;\n};\n\nv2f vert(appdata v)\n{\nv2f o;\no.vertex = UnityObjectToClipPos(v.vertex);\no.uv = v.uv;\nreturn o;\n}\n\nfixed4 frag(v2f i): SV_Target\n{\nfloat4 col = float4(1, 0, 0, 1);\nif (i.vertex.x > _CameraDepthNormalsTexture_TexelSize.z / (1 / _Seperate))\n{\nfixed3 tex = tex2D(_MainTex, i.uv).rgb;\nfixed4 dn = tex2D(_CameraDepthNormalsTexture, i.uv);\nfloat depth;\nfloat3 normal;\nDecodeDepthNormal(dn, depth, normal);\ncol = float4(normal, 1);\n}\nelse\n{\ncol = tex2D(_MainTex, i.uv);\n}\nreturn col;\n}\nENDCG\n\n}\n}\n}");
                }

                return _depthNormal;
            }
        }
    }
    // animated bool for GUI
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
    internal class URPData : ICloneable
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
    internal class HDRPData : ICloneable
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
    internal class See1ViewData : ICloneable
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
        public See1ViewData(string name)
        {
            this.name = name;
        }

        public object Clone()
        {
            return this.MemberwiseClone();
        }
    }
    // recent list
    [Serializable]
    internal class Recent<T> where T : UnityEngine.Object
    {
        private int _maxSize = 10;
        private List<string> _list = new List<string>();
        public int size { get { return _list.Count; } }
        private string key = "see1view.recent.";
        public Action<T> onClickEvent;

        public Recent(int maxSize)
        {
            this._maxSize = maxSize;
            this.key += typeof(T).ToString().ToLower();
            Load();
        }

        public void Add(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!_list.Contains(path))
            {
                if (_list.Count > _maxSize)
                {
                    _list = _list.GetRange(1, _list.Count - 1);
                }
                _list.Add(path);
            }
            Save();
        }

        public T Get(int index)
        {
            return (T)AssetDatabase.LoadAssetAtPath<T>(_list[index]);
        }

        internal string GetName(int index)
        {
            if (index < _list.Count)
            {
                return Path.GetFileNameWithoutExtension(_list[index]);
            }
            return string.Empty;
        }

        public void Clear()
        {
            _list.Clear();
            Save();
        }

        void Load()
        {
            string temp = EditorPrefs.GetString(key);
            string[] tempArray = temp.Split(",".ToCharArray());

            for (int i = 0; i < tempArray.Length; i++)
            {
                if (i <= _maxSize) _list.Add(tempArray[i]);
            }
        }

        void Save()
        {
            string temp = string.Empty;
            //for (int i = 0; i < _list.Count; i++)
            //{
            //    if (i != _list.Count - 1)
            //        temp += _list[i].ToString() + ",";//note that the last character you add
            //                                          //is important
            //    else
            //        temp += _list[i].ToString();
            //}
            temp = string.Join(",", _list);
            EditorPrefs.SetString(key, temp);
        }

        public void OnGUI()
        {
            using (EditorHelper.Colorize.Do(Color.white, Color.red))
            {
                if (GUILayout.Button("Clear", EditorStyles.miniButton))
                {
                    Clear();
                }
            }
            for (int i = size - 1; i > 0; --i)
            {
                {
                    if (GUILayout.Button(new GUIContent(GetName(i), _list[i]), EditorStyles.miniButton))
                    {
                        var obj = (T)AssetDatabase.LoadAssetAtPath<T>(_list[i]);
                        if (obj)
                        {
                            onClickEvent?.Invoke(obj);
                        }
                    }
                }
            }
        }

    }
    // all configuration and settings
    [Serializable]
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

        public List<See1ViewData> dataList = new List<See1ViewData>();

        public See1ViewData current
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
                    targetPath = "Assets/Editor/See1ViewData.json";
                    break;
                case SaveType.UserSetting:
                    targetPath = "UserSettings/See1ViewData.json";
                    break;
                case SaveType.EditorPreferences:
                    targetPath = "Assets/Editor/See1ViewData.json";
                    break;
                case SaveType.Registry:
                    targetPath = "Assets/Editor/See1ViewData.json";
                    break;
            }
            //UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget();
            string systemProjectPath = Application.dataPath.Replace("Assets", "");
            DirectoryInfo di = new DirectoryInfo(systemProjectPath + Path.GetDirectoryName(targetPath));
            if (!di.Exists) di.Create();
            return targetPath;
        }

        public bool Add(string name)
        {
            bool canAdd = CheckName(name);
            while (!canAdd)
            {
                name += "_1";
                canAdd = CheckName(name);
            }

            See1ViewData data = new See1ViewData(name);
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

        public bool Remove(See1ViewData data)
        {
            if (dataList.Contains(data))
            {
                dataList.Remove(data);
                Mathf.Clamp(dataIndex -= 1, 0, dataList.Count);
                return true;
            }

            return false;
        }

        private static DataManager Load()
        {
            _instance = new DataManager();
            string data = string.Empty;
            if (File.Exists(GetPath()))
            {
                data = File.ReadAllText(GetPath());
                JsonUtility.FromJsonOverwrite(data, _instance);
                _isDirty = false;
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
            var json = JsonUtility.ToJson(instance, true);
            File.WriteAllText(GetPath(), json);
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
            See1ViewData data = current.Clone() as See1ViewData;
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
                        Notice.Log(string.Format("Data Chaneged to {0}", instance.current.name));
                    }
                }
            }
        }
    }
    // camera information
    [Serializable]
    internal class View
    {
        public string name;
        public Vector2 rotation;
        public float distance;
        public Vector3 pivot;
        public float fieldOfView = 30f;

        public View(Vector2 rotation, float distance, Vector3 pivot, float fieldOfView)
        {
            this.name = string.Empty;
            this.rotation = rotation;
            this.distance = distance;
            this.pivot = pivot;
            this.fieldOfView = fieldOfView;
        }

        public View(Camera camera)
        {
            this.name = camera.name;
            this.rotation = new Vector2(camera.transform.rotation.eulerAngles.y, camera.transform.rotation.eulerAngles.x);
            var distanceToZero = Vector3.Distance(camera.transform.position, Vector3.zero); //카메라 뷰 타겟 거리로 적당히 쓸만한 거리
            this.pivot = camera.ScreenToWorldPoint(new Vector3(0.5f, 0.5f, 0)) + camera.transform.rotation * Vector3.forward * distanceToZero;
            this.distance = Vector3.Distance(camera.transform.position, this.pivot);
            this.fieldOfView = camera.fieldOfView;
        }
    }
    // scene lighting infomation
    [Serializable]
    internal class Lighting
    {
        [Serializable]
        public class LightInfo
        {
            public Vector2 position = Vector2.zero;
            public Quaternion rotation = Quaternion.identity;
            public Color lightColor = Color.white;
            public float intensity = 1;
        }

        public string name = string.Empty;
        public List<LightInfo> lightList = new List<LightInfo>();
        public Color ambientSkyColor = Color.gray;
        public string cubemapPath = string.Empty;
    }

    internal class Quad
    {
        static Mesh mesh;

        static Quad()
        {
            if (mesh == null)
            {
                mesh = new Mesh();
                float halfWidth = 1 / 2f;
                float halfHeight = 1 / 2f;
                Vector3[] vertices = new Vector3[4]
                {
                    new Vector3(-halfWidth, 0, -halfHeight),
                    new Vector3(halfHeight, 0, -halfHeight),
                    new Vector3(-halfHeight, 0, halfHeight),
                    new Vector3(halfWidth, 0, halfHeight)
                };
                mesh.vertices = vertices;

                int[] tris = new int[6]
                {
                    // lower left triangle
                    0, 2, 1,
                    // upper right triangle
                    2, 3, 1
                };
                mesh.triangles = tris;

                Vector3[] normals = new Vector3[4]
                {
                    Vector3.up,
                    Vector3.up,
                    Vector3.up,
                    Vector3.up
                };
                mesh.normals = normals;

                Vector2[] uv = new Vector2[4]
                {
                    new Vector2(0, 0),
                    new Vector2(1, 0),
                    new Vector2(0, 1),
                    new Vector2(1, 1)
                };
                mesh.uv = uv;
            }
        }
        public static Mesh Get()
        {
            return mesh;
        }
    }

    internal class DefaultMaterial
    {
        static Material _builtIn;
        static Material _universal;
        static Material _highDefinition;

        internal static Material Get(RenderPipelineMode renderPipelineMode)
        {
            switch (renderPipelineMode)
            {
                case RenderPipelineMode.BuiltIn:
                    if (_builtIn == null)
                        _builtIn = new Material(Shader.Find("Standard"));
                    return _builtIn;
                case RenderPipelineMode.Universal:
                    if (_universal == null)
                        _universal = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                    return _universal;
                case RenderPipelineMode.HighDefinition:
                    if (_highDefinition == null)
                        _highDefinition = new Material(Shader.Find("HDRP/Lit"));
                    return _highDefinition;
                default: 
                    return _builtIn;
            }
        }
    }

    // model animation frame information
    [Serializable]
    internal class Steel
    {
        public string clipPath;
        public double time;
        private AnimationClip _animationClip;
        public AnimationClip animationClip
        {
            get
            {
                if (!_animationClip)
                    _animationClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                return _animationClip;
            }
            set
            {
                _animationClip = value;
                clipPath = AssetDatabase.GetAssetPath(_animationClip);
            }
        }

        public Steel(AnimationClip clip, double time)
        {
            this.animationClip = clip;
            this.time = time;
        }
    }

    // view target object. model, particle, etc
    class TargetInfo
    {
        public string assetPath;
        private StringBuilder sb = new StringBuilder();
        public Bounds bounds;
        public List<Renderer> renderers = new List<Renderer>();
        public List<Transform> bones = new List<Transform>();
        public List<Material> materials = new List<Material>();
        public List<Animator> animators = new List<Animator>();
        public List<MeshRenderer> meshRenderers = new List<MeshRenderer>();
        public List<SkinnedMeshRenderer> skinnedMeshRenderers = new List<SkinnedMeshRenderer>();
        public List<ParticleSystem> particleSystems = new List<ParticleSystem>();
        public List<MonoBehaviour> behaviours = new List<MonoBehaviour>();

        public List<ParticleSystemRenderer> particleSystemRenderers = new List<ParticleSystemRenderer>();
        //public Mesh[] meshes;

        void Cleanup()
        {
            sb.Length = 0;
            bounds = new Bounds();
            renderers.Clear();
            bones.Clear();
            materials.Clear();
            animators = null;
            meshRenderers = null;
            skinnedMeshRenderers = null;
            particleSystems = null;
            particleSystemRenderers = null;
            behaviours = null;
        }

        public void Init(GameObject source,GameObject instance)
        {
            Cleanup();
            var srcPrefab = PrefabUtility.GetCorrespondingObjectFromSource(source);
            assetPath = srcPrefab ? AssetDatabase.GetAssetPath(srcPrefab) : AssetDatabase.GetAssetPath(source);
            sb.Append(source.name);
            sb.Append("\n");
            animators = instance.GetComponentsInChildren<Animator>().ToList();
            renderers = instance.GetComponentsInChildren<Renderer>().ToList();
            meshRenderers = instance.GetComponentsInChildren<MeshRenderer>().ToList();
            skinnedMeshRenderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>().ToList();
            particleSystems = instance.GetComponentsInChildren<ParticleSystem>().ToList();
            particleSystemRenderers = instance.GetComponentsInChildren<ParticleSystemRenderer>().ToList();
            behaviours = instance.GetComponentsInChildren<MonoBehaviour>().ToList();

            foreach (var renderer in renderers)
            {
                materials.AddRange(renderer.sharedMaterials);
                bounds.Encapsulate(renderer.bounds);
            }

            materials = materials.Where(x => x != null).Distinct().ToList();

            if (animators.Count > 0)
            {
                sb.Append(string.Format("Animators : {0}\n", animators.Count().ToString()));
            }

            if (meshRenderers.Count > 0)
            {
                sb.Append(string.Format("MeshRenderer : {0}\n", meshRenderers.Count.ToString()));
            }

            if (skinnedMeshRenderers.Count > 0)
            {
                bones.AddRange(skinnedMeshRenderers.SelectMany(x => x.bones).Distinct());
                sb.Append(string.Format("SkinnedMeshRenderer : {0}\n", skinnedMeshRenderers.Count.ToString()));
                sb.Append(string.Format("Bones : {0}\n",
                    skinnedMeshRenderers.SelectMany(x => x.bones).Distinct().Count().ToString()));
            }

            if (particleSystems.Count > 0)
            {
                foreach (var ps in particleSystems)
                {
                    ParticleSystemRenderer component = ps.GetComponent<ParticleSystemRenderer>();
                    ps.Simulate(1, true, true, false);
                    bounds.Encapsulate(component.bounds);
                    ps.Clear();
                    ps.Stop();
                }

                sb.Append(string.Format("ParticleSystem : {0}\n", particleSystems.Count.ToString()));
                if (particleSystemRenderers.Count > 0)
                {
                    sb.Append(string.Format("ParticleSystemRenderer : {0}\n",
                        particleSystemRenderers.Count.ToString()));
                }
            }

            sb.Append(string.Format("Materials : {0}\n",
                renderers.SelectMany(x => x.sharedMaterials).Distinct().Count().ToString()));

            if (behaviours.Count > 0)
            {
                sb.Append(string.Format("Monobehaviour : {0}\n", behaviours.Count.ToString()));
            }
        }

        public string GetMeshInfo(Mesh target)
        {
            //namespace UnityEditor
            //{
            //  internal sealed class InternalMeshUtil
            //  {
            //    public static extern int GetPrimitiveCount(Mesh mesh);
            //    public static extern int CalcTriangleCount(Mesh mesh);
            //    public static extern bool HasNormals(Mesh mesh);
            //    public static extern string GetVertexFormat(Mesh mesh);
            //    public static extern float GetCachedMeshSurfaceArea(MeshRenderer meshRenderer);
            //  }
            //}
            Type internalMeshUtil = Type.GetType("InternalMeshUtil");
            MethodInfo getPrimitiveCount = internalMeshUtil.GetMethod("GetPrimitiveCount", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo getVertexFormat = internalMeshUtil.GetMethod("GetVertexFormat", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            string str = target.vertexCount.ToString() + " verts, " + (object)getPrimitiveCount.Invoke(this, new object[] { target }) + " tris";
            int subMeshCount = target.subMeshCount;
            if (subMeshCount > 1)
                str = str + ", " + (object)subMeshCount + " submeshes";
            int blendShapeCount = target.blendShapeCount;
            if (blendShapeCount > 1)
                str = str + ", " + (object)blendShapeCount + " blendShapes";
            return str + "\n" + getVertexFormat.Invoke(this, new object[] { target });
        }

        public string Print()
        {
            return sb.ToString();
        }
    }
    // override quality setting in scope
    class QualitySettingsOverrider : IDisposable
    {
        private UnityEngine.ShadowQuality _shadows;
        private UnityEngine.ShadowResolution _shadowResolution;
        private ShadowProjection _shadowProjection;
        private float _shadowDistance;
        private ShadowmaskMode _shadowmaskMode;

        public QualitySettingsOverrider()
        {
            _shadows = QualitySettings.shadows;
            QualitySettings.shadows = UnityEngine.ShadowQuality.All;
            _shadowResolution = QualitySettings.shadowResolution;
            QualitySettings.shadowResolution = UnityEngine.ShadowResolution.VeryHigh;
            _shadowProjection = QualitySettings.shadowProjection;
            QualitySettings.shadowProjection = ShadowProjection.CloseFit;
            _shadowDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = 10;
            _shadowmaskMode = QualitySettings.shadowmaskMode;
            QualitySettings.shadowmaskMode = ShadowmaskMode.DistanceShadowmask;
        }

        public QualitySettingsOverrider(UnityEngine.ShadowQuality shadows, UnityEngine.ShadowResolution shadowResolution,
            ShadowProjection shadowProjection, float shadowDistance, ShadowmaskMode shadowmaskMode)
        {
            _shadows = QualitySettings.shadows;
            QualitySettings.shadows = shadows;
            _shadowResolution = QualitySettings.shadowResolution;
            QualitySettings.shadowResolution = shadowResolution;
            _shadowProjection = QualitySettings.shadowProjection;
            QualitySettings.shadowProjection = shadowProjection;
            _shadowDistance = QualitySettings.shadowDistance;
            QualitySettings.shadowDistance = shadowDistance;
            _shadowmaskMode = QualitySettings.shadowmaskMode;
            QualitySettings.shadowmaskMode = shadowmaskMode;
        }

        public void Dispose()
        {
            QualitySettings.shadows = _shadows;
            QualitySettings.shadowResolution = _shadowResolution;
            QualitySettings.shadowDistance = _shadowDistance;
            QualitySettings.shadowProjection = _shadowProjection;
            QualitySettings.shadowmaskMode = _shadowmaskMode;
        }
    }
    // override rendersetting in scope
    class RenderSettingsOverrider : IDisposable
    {
        [Serializable]
        public class RenderSettingsData
        {
            public bool fog = false;
            public float fogStartDistance = 0f;
            public float fogEndDistance = 300f;
            public FogMode fogMode = FogMode.ExponentialSquared;
            public Color fogColor = Color.gray;
            public float fogDensity = 0.01f;
            public AmbientMode ambientMode = AmbientMode.Skybox;
            public Color ambientSkyColor = new Color(0.212f, 0.227f, 0.259f, 1.000f);
            public Color ambientEquatorColor = new Color(0.114f, 0.125f, 0.133f, 1.000f);
            public Color ambientGroundColor = new Color(0.047f, 0.043f, 0.035f, 1.000f);
            public float ambientIntensity = 1f;
            public Color ambientLight = new Color(0.212f, 0.227f, 0.259f, 1.000f);
            public Color subtractiveShadowColor = new Color(0.420f, 0.478f, 0.627f, 1.000f);
            public Material skybox = null;
            public Light sun = null;
            public SphericalHarmonicsL2 ambientProbe = new SphericalHarmonicsL2();
            public Cubemap customReflection = null;
            public float reflectionIntensity = 1f;
            public int reflectionBounces = 1;
            public DefaultReflectionMode defaultReflectionMode = DefaultReflectionMode.Skybox;
            public int defaultReflectionResolution = 128;
            public float haloStrength = 0.5f;
            public float flareStrength = 1f;
            public float flareFadeSpeed = 3f;

            public void CopyToRenderSettings()
            {
                RenderSettings.fog = fog;
                RenderSettings.fogDensity = fogDensity;
                RenderSettings.fogColor = fogColor;
                RenderSettings.skybox = skybox;
                RenderSettings.sun = sun;
                RenderSettings.ambientIntensity = ambientIntensity;
                RenderSettings.ambientLight = ambientLight;
                RenderSettings.ambientMode = ambientMode;
                RenderSettings.customReflection = customReflection;
                RenderSettings.fogMode = fogMode;
                RenderSettings.haloStrength = haloStrength;
                RenderSettings.reflectionBounces = reflectionBounces;
                RenderSettings.reflectionIntensity = reflectionIntensity;
                RenderSettings.ambientEquatorColor = ambientEquatorColor;
                RenderSettings.ambientGroundColor = ambientGroundColor;
                RenderSettings.ambientSkyColor = ambientSkyColor;
                RenderSettings.defaultReflectionMode = defaultReflectionMode;
                RenderSettings.defaultReflectionResolution = defaultReflectionResolution;
                RenderSettings.flareFadeSpeed = flareFadeSpeed;
                RenderSettings.fogEndDistance = fogEndDistance;
                RenderSettings.fogStartDistance = fogStartDistance;
                RenderSettings.subtractiveShadowColor = subtractiveShadowColor;
            }

            public void CopyFromRenderSettings()
            {
                fog = RenderSettings.fog = fog;
                fogDensity = RenderSettings.fogDensity;
                fogColor = RenderSettings.fogColor;
                skybox = RenderSettings.skybox;
                sun = RenderSettings.sun;
                ambientIntensity = RenderSettings.ambientIntensity;
                ambientLight = RenderSettings.ambientLight;
                ambientMode = RenderSettings.ambientMode;
                customReflection = (Cubemap)RenderSettings.customReflection;
                fogMode = RenderSettings.fogMode;
                haloStrength = RenderSettings.haloStrength;
                reflectionBounces = RenderSettings.reflectionBounces;
                reflectionIntensity = RenderSettings.reflectionIntensity;
                ambientEquatorColor = RenderSettings.ambientEquatorColor;
                ambientGroundColor = RenderSettings.ambientGroundColor;
                ambientSkyColor = RenderSettings.ambientSkyColor;
                defaultReflectionMode = RenderSettings.defaultReflectionMode;
                defaultReflectionResolution = RenderSettings.defaultReflectionResolution;
                flareFadeSpeed = RenderSettings.flareFadeSpeed;
                fogEndDistance = RenderSettings.fogEndDistance;
                fogStartDistance = RenderSettings.fogStartDistance;
                subtractiveShadowColor = RenderSettings.subtractiveShadowColor;
            }
        }

        RenderSettingsData savedRenderSettings = new RenderSettingsData();

        public RenderSettingsOverrider(AmbientMode ambientMode, Color ambientSkyColor, Material skybox)
        {
            savedRenderSettings.ambientMode = RenderSettings.ambientMode;
            savedRenderSettings.ambientSkyColor = RenderSettings.ambientSkyColor;
            savedRenderSettings.skybox = RenderSettings.skybox;
            RenderSettings.skybox = skybox;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientSkyColor = ambientSkyColor;
        }

        public RenderSettingsOverrider(RenderSettingsData c)
        {
            savedRenderSettings.CopyFromRenderSettings(); //backup
            c.CopyToRenderSettings();
        }

        public void Dispose()
        {
            savedRenderSettings.CopyToRenderSettings();
        }
    }
    // override renderpipeline in scope. 
    class RenderPipelineOverrider : IDisposable
    {
        private RenderPipelineAsset _renderPipeline;
        private bool _pipelineMatches;
        public RenderPipelineOverrider(RenderPipelineAsset renderPipelineAsset)
        {
            if (renderPipelineAsset && GraphicsSettings.defaultRenderPipeline)
            {
                if (renderPipelineAsset.GetType() == GraphicsSettings.defaultRenderPipeline.GetType())
                {
                    _pipelineMatches = true;
                    _renderPipeline = GraphicsSettings.defaultRenderPipeline;
                    GraphicsSettings.renderPipelineAsset = renderPipelineAsset;
                }
            }
        }

        public void Dispose()
        {
            if (_renderPipeline)
            {
                if (_pipelineMatches)
                {
                    GraphicsSettings.renderPipelineAsset = _renderPipeline;
                }
            }
        }
    }
    // show hide object in scope
    struct ShowHideObjectScope : IDisposable
    {
        private Dictionary<Renderer, bool> rendererStateDic;

        public ShowHideObjectScope(GameObject root, bool enabled)
        {
            rendererStateDic = new Dictionary<Renderer, bool>();
            if (root)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                if (renderers != null)
                {
                    if (renderers.Length > 0)
                    {
                        for (int i = 0; i < renderers.Length; i++)
                        {
                            rendererStateDic.Add(renderers[i], renderers[i].enabled);
                            renderers[i].enabled = enabled;
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            foreach (var pair in rendererStateDic)
            {
                pair.Key.enabled = pair.Value;
            }
        }
    }

    struct ShowHideRendererScope : IDisposable
    {
        Renderer renderer;
        bool enabled;

        public ShowHideRendererScope(Renderer renderer,bool enabled)
        {
            this.renderer = renderer;
            this.enabled = renderer.enabled;
            renderer.enabled = enabled;
        }

        public void Dispose()
        {
            renderer.enabled = enabled;
        }
    }

    // simple command buffer manager
    class CommandBufferManager
    {
        class Blitter
        {
            public Camera camera;
            public CommandBuffer commandBuffer;
            public CameraEvent cameraEvent;
            public RenderTexture rt;
            public Material mat;
            //public RenderPipelineAsset pipelineAsset;

            public Blitter(Camera cam, CameraEvent cameraEvent, Material mat)
            {
                this.camera = cam;
                this.cameraEvent = cameraEvent;
                commandBuffer = new CommandBuffer();
                this.mat = mat;
            }

            public void Blit()
            {
                rt = RenderTexture.GetTemporary(camera.targetTexture.width, camera.targetTexture.height, 24);
                camera.AddCommandBuffer(cameraEvent, commandBuffer);
                commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, rt, mat);
                commandBuffer.Blit(rt, BuiltinRenderTextureType.CameraTarget);

            }
        }

        private List<Blitter> blitterList = new List<Blitter>();

        //private DepthTextureMode _mode = DepthTextureMode.None;
        private Camera _camera;

        public CommandBufferManager(Camera camera)
        {
            this._camera = camera;
        }

        public void Add(DepthTextureMode mode, Material mat)
        {
            //this._mode = mode;
            _camera.depthTextureMode = mode;
            foreach (var blitter in blitterList)
            {
                blitter.rt =
                    RenderTexture.GetTemporary(_camera.targetTexture.width, _camera.targetTexture.height, 24);
                _camera.AddCommandBuffer(blitter.cameraEvent, blitter.commandBuffer);
                blitter.commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, blitter.rt, mat);
                blitter.commandBuffer.Blit(blitter.rt, BuiltinRenderTextureType.CameraTarget);
            }
        }

        public static void RemoveBufferFromAllEvent(Camera camera, CommandBuffer buffer)
        {
            camera.RemoveCommandBuffer(CameraEvent.BeforeDepthTexture, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterDepthTexture, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeDepthNormalsTexture, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterDepthNormalsTexture, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeGBuffer, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterGBuffer, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeLighting, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterLighting, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeFinalPass, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterFinalPass, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterImageEffectsOpaque, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeSkybox, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterSkybox, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffects, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterImageEffects, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterEverything, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeReflections, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterReflections, buffer);
            camera.RemoveCommandBuffer(CameraEvent.BeforeHaloAndLensFlares, buffer);
            camera.RemoveCommandBuffer(CameraEvent.AfterHaloAndLensFlares, buffer);
        }
    }
    // calculate fps info
    class FPS
    {
        static string formatedString = "{0} FPS ({1}ms)";

        static float ms
        {
            get { return (float)System.Math.Round(1000f / fps, 1); }
        }

        public static float updateInterval = 0.25f;
        static float elapsedTime = 0;
        static float fps = 0.0F;

        public static void Calculate(float deltaTime)
        {
            elapsedTime += deltaTime;
            if (elapsedTime / updateInterval > 1)
            {
                fps = 1 / deltaTime;
                elapsedTime = 0;
            }

            fps = (float)System.Math.Round(fps, 1);
        }

        public static string GetString()
        {
            return string.Format(formatedString, fps.ToString(), ms.ToString());
        }
    }
    // texture utilities. not use.
    class TexUtil
    {
        public enum ImageFilterMode : int
        {
            Nearest = 0,
            Biliner = 1,
            Average = 2
        }

        public static Texture2D ResizeTexture(Texture2D pSource, ImageFilterMode pFilterMode, Vector2 size)
        {

            //*** Variables
            int i;

            //*** Get All the source pixels
            Color[] aSourceColor = pSource.GetPixels(0);
            Vector2 vSourceSize = new Vector2(pSource.width, pSource.height);

            //*** Calculate New Size
            float xWidth = Mathf.RoundToInt((float)size.x);
            float xHeight = Mathf.RoundToInt((float)size.y);

            //*** Make New
            Texture2D oNewTex = new Texture2D((int)xWidth, (int)xHeight, TextureFormat.RGBA32, false);

            //*** Make destination array
            int xLength = (int)xWidth * (int)xHeight;
            Color[] aColor = new Color[xLength];

            Vector2 vPixelSize = new Vector2(vSourceSize.x / xWidth, vSourceSize.y / xHeight);

            //*** Loop through destination pixels and process
            Vector2 vCenter = new Vector2();
            for (i = 0; i < xLength; i++)
            {

                //*** Figure out x&y
                float xX = (float)i % xWidth;
                float xY = Mathf.Floor((float)i / xWidth);

                //*** Calculate Center
                vCenter.x = (xX / xWidth) * vSourceSize.x;
                vCenter.y = (xY / xHeight) * vSourceSize.y;

                //*** Do Based on mode
                //*** Nearest neighbour (testing)
                if (pFilterMode == ImageFilterMode.Nearest)
                {

                    //*** Nearest neighbour (testing)
                    vCenter.x = Mathf.Round(vCenter.x);
                    vCenter.y = Mathf.Round(vCenter.y);

                    //*** Calculate source index
                    int xSourceIndex = (int)((vCenter.y * vSourceSize.x) + vCenter.x);

                    //*** Copy Pixel
                    aColor[i] = aSourceColor[xSourceIndex];
                }

                //*** Bilinear
                else if (pFilterMode == ImageFilterMode.Biliner)
                {

                    //*** Get Ratios
                    float xRatioX = vCenter.x - Mathf.Floor(vCenter.x);
                    float xRatioY = vCenter.y - Mathf.Floor(vCenter.y);

                    //*** Get Pixel index's
                    int xIndexTL = (int)((Mathf.Floor(vCenter.y) * vSourceSize.x) + Mathf.Floor(vCenter.x));
                    int xIndexTR = (int)((Mathf.Floor(vCenter.y) * vSourceSize.x) + Mathf.Ceil(vCenter.x));
                    int xIndexBL = (int)((Mathf.Ceil(vCenter.y) * vSourceSize.x) + Mathf.Floor(vCenter.x));
                    int xIndexBR = (int)((Mathf.Ceil(vCenter.y) * vSourceSize.x) + Mathf.Ceil(vCenter.x));

                    //*** Calculate Color
                    aColor[i] = Color.Lerp(
                        Color.Lerp(aSourceColor[xIndexTL], aSourceColor[xIndexTR], xRatioX),
                        Color.Lerp(aSourceColor[xIndexBL], aSourceColor[xIndexBR], xRatioX),
                        xRatioY
                    );
                }

                //*** Average
                else if (pFilterMode == ImageFilterMode.Average)
                {

                    //*** Calculate grid around point
                    int xXFrom = (int)Mathf.Max(Mathf.Floor(vCenter.x - (vPixelSize.x * 0.5f)), 0);
                    int xXTo = (int)Mathf.Min(Mathf.Ceil(vCenter.x + (vPixelSize.x * 0.5f)), vSourceSize.x);
                    int xYFrom = (int)Mathf.Max(Mathf.Floor(vCenter.y - (vPixelSize.y * 0.5f)), 0);
                    int xYTo = (int)Mathf.Min(Mathf.Ceil(vCenter.y + (vPixelSize.y * 0.5f)), vSourceSize.y);

                    //*** Loop and accumulate
                    //Vector4 oColorTotal = new Vector4();
                    Color oColorTemp = new Color();
                    float xGridCount = 0;
                    for (int iy = xYFrom; iy < xYTo; iy++)
                    {
                        for (int ix = xXFrom; ix < xXTo; ix++)
                        {

                            //*** Get Color
                            oColorTemp += aSourceColor[(int)(((float)iy * vSourceSize.x) + ix)];

                            //*** Sum
                            xGridCount++;
                        }
                    }

                    //*** Average Color
                    aColor[i] = oColorTemp / (float)xGridCount;
                }
            }

            //*** Set Pixels
            oNewTex.SetPixels(aColor);
            oNewTex.Apply();

            //*** Return
            return oNewTex;
        }

        public static Texture2D ApplyGammaCorrection(Texture2D src)
        {
            Color[] srcColors = src.GetPixels(0);
            Texture2D newTex = new Texture2D((int)src.width, (int)src.height, TextureFormat.RGBA32, false);
            int pixelCount = (int)src.width * (int)src.height;
            Color[] newColors = new Color[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                newColors[i] = srcColors[i].gamma;
            }

            newTex.SetPixels(newColors);
            newTex.Apply();
            return newTex;
        }
    }
    // simple grid mesh builder
    class Grid
    {
        private static List<Vector3> verticies;
        private static List<int> indicies;
        private static Mesh mesh;

        public static Mesh Get(int size)
        {
            if (mesh == null) mesh = new Mesh();
            if (indicies == null) indicies = new List<int>();
            if (verticies == null) verticies = new List<Vector3>();

            mesh.Clear();
            verticies.Clear();
            indicies.Clear();

            for (int i = 0; i < size; i++)
            {
                verticies.Add(new Vector3(i, 0, 0));
                verticies.Add(new Vector3(i, 0, size));

                indicies.Add(4 * i + 0);
                indicies.Add(4 * i + 1);

                verticies.Add(new Vector3(0, 0, i));
                verticies.Add(new Vector3(size, 0, i));

                indicies.Add(4 * i + 2);
                indicies.Add(4 * i + 3);
            }

            mesh.vertices = verticies.ToArray();
            mesh.SetIndices(indicies.ToArray(), MeshTopology.Lines, 0);
            return mesh;
        }
    }
    // simple hierachy view
    class TransformTreeView : TreeView
    {
        Scene scene;
        public Action<GameObject> onDragObject;

        public TransformTreeView(Scene scene, TreeViewState state)
            : base(state)
        {
            this.scene = scene;
            Reload();
        }

        protected override TreeViewItem BuildRoot()
        {
            return new TreeViewItem { id = 0, depth = -1 };
        }


        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            var rows = GetRows() ?? new List<TreeViewItem>(200);

            //Scene scene = SceneManager.GetSceneAt (0);

            // We use the GameObject instanceIDs as ids for items as we want to 
            // select the game objects and not the transform components.
            rows.Clear();
            var gameObjectRoots = scene.GetRootGameObjects();
            foreach (var gameObject in gameObjectRoots)
            {
                var item = CreateTreeViewItemForGameObject(gameObject);
                root.AddChild(item);
                rows.Add(item);
                if (gameObject.transform.childCount > 0)
                {
                    if (IsExpanded(item.id))
                    {
                        AddChildrenRecursive(gameObject, item, rows);
                    }
                    else
                    {
                        item.children = CreateChildListForCollapsedParent();
                    }
                }
            }

            SetupDepthsFromParentsAndChildren(root);
            return rows;
        }

        void AddChildrenRecursive(GameObject go, TreeViewItem item, IList<TreeViewItem> rows)
        {
            int childCount = go.transform.childCount;

            item.children = new List<TreeViewItem>(childCount);
            for (int i = 0; i < childCount; ++i)
            {
                var childTransform = go.transform.GetChild(i);
                var childItem = CreateTreeViewItemForGameObject(childTransform.gameObject);
                item.AddChild(childItem);
                rows.Add(childItem);

                if (childTransform.childCount > 0)
                {
                    if (IsExpanded(childItem.id))
                    {
                        AddChildrenRecursive(childTransform.gameObject, childItem, rows);
                    }
                    else
                    {
                        childItem.children = CreateChildListForCollapsedParent();
                    }
                }
            }
        }

        static TreeViewItem CreateTreeViewItemForGameObject(GameObject gameObject)
        {
            // We can use the GameObject instanceID for TreeViewItem id, as it ensured to be unique among other items in the tree.
            // To optimize reload time we could delay fetching the transform.name until it used for rendering (prevents allocating strings 
            // for items not rendered in large trees)
            // We just set depth to -1 here and then call SetupDepthsFromParentsAndChildren at the end of BuildRootAndRows to set the depths.
            return new TreeViewItem(gameObject.GetInstanceID(), -1, gameObject.name);
        }

        protected override IList<int> GetAncestors(int id)
        {
            // The backend needs to provide us with this info since the item with id
            // may not be present in the rows

            List<int> ancestors = new List<int>();
            var go = GetGameObject(id);
            if (!go) return ancestors;
            var transform = GetGameObject(id).transform;
            while (transform.parent != null)
            {
                ancestors.Add(transform.parent.gameObject.GetInstanceID());
                transform = transform.parent;
            }

            return ancestors;
        }

        protected override IList<int> GetDescendantsThatHaveChildren(int id)
        {
            Stack<Transform> stack = new Stack<Transform>();

            var start = GetGameObject(id).transform;
            stack.Push(start);

            var parents = new List<int>();
            while (stack.Count > 0)
            {
                Transform current = stack.Pop();
                parents.Add(current.gameObject.GetInstanceID());
                for (int i = 0; i < current.childCount; ++i)
                {
                    if (current.childCount > 0)
                        stack.Push(current.GetChild(i));
                }
            }

            return parents;
        }

        GameObject GetGameObject(int instanceID)
        {
            return (GameObject)EditorUtility.InstanceIDToObject(instanceID);
        }

        // Custom GUI

        protected override void RowGUI(RowGUIArgs args)
        {
            Event evt = Event.current;
            extraSpaceBeforeIconAndLabel = 18f;

            // GameObject isStatic toggle 
            var gameObject = GetGameObject(args.item.id);
            if (gameObject == null)
                return;

            Rect r = args.rowRect;
            r.x += GetContentIndent(args.item);
            r.width = 16f;

            // Ensure row is selected before using the toggle (usability)
            if (evt.type == EventType.MouseDown && r.Contains(evt.mousePosition))
                SelectionClick(args.item, false);

            EditorGUI.BeginChangeCheck();
            bool activeInHierarchy = EditorGUI.Toggle(r, gameObject.activeInHierarchy);
            if (EditorGUI.EndChangeCheck())
                gameObject.SetActive(activeInHierarchy);

            r.x += 16f;
            r.width = args.rowRect.width;
            EditorGUI.DropShadowLabel(r, args.item.displayName, EditorStyles.whiteMiniLabel);
            r.x += r.width - 60;
            r.width = 16;
            r.height = 16;

            //Delete root gameObject only
            if (gameObject.transform.parent == null)
            {

                if (GUI.Button(r, "x", EditorStyles.miniButton))
                {

                }
            }

            //base.RowGUI(args);
        }

        // Selection

        protected override void SelectionChanged(IList<int> selectedIds)
        {
            Selection.instanceIDs = selectedIds.ToArray();
        }

        // Reordering

        protected override bool CanStartDrag(CanStartDragArgs args)
        {
            return true;
        }

        protected override void SetupDragAndDrop(SetupDragAndDropArgs args)
        {
            DragAndDrop.PrepareStartDrag();

            var sortedDraggedIDs = SortItemIDsInRowOrder(args.draggedItemIDs);

            List<Object> objList = new List<Object>(sortedDraggedIDs.Count);
            foreach (var id in sortedDraggedIDs)
            {
                Object obj = EditorUtility.InstanceIDToObject(id);
                if (obj != null)
                    objList.Add(obj);
            }

            DragAndDrop.objectReferences = objList.ToArray();

            string title = objList.Count > 1 ? "<Multiple>" : objList[0].name;
            DragAndDrop.StartDrag(title);
        }

        protected override DragAndDropVisualMode HandleDragAndDrop(DragAndDropArgs args)
        {
            // First check if the dragged objects are GameObjects
            var draggedObjects = DragAndDrop.objectReferences;
            var transforms = new List<Transform>(draggedObjects.Length);
            foreach (var obj in draggedObjects)
            {
                var go = obj as GameObject;
                if (go == null)
                {
                    return DragAndDropVisualMode.None;
                }

                if (!AssetDatabase.Contains(go)) continue; //Project View Asset
                                                           //프로젝트 뷰에서 드래그하면 인스턴스를 만들어 Add 하도록 해봄
                if (onDragObject != null)
                {
                    onDragObject(go);
                }

                //transforms.Add(go.transform);
            }

            // Filter out any unnecessary transforms before the reparent operation
            RemoveItemsThatAreDescendantsFromOtherItems(transforms);

            // Reparent
            if (args.performDrop)
            {
                switch (args.dragAndDropPosition)
                {
                    case DragAndDropPosition.UponItem:
                    case DragAndDropPosition.BetweenItems:
                        Transform parent = args.parentItem != null
                            ? GetGameObject(args.parentItem.id).transform
                            : null;

                        if (!IsValidReparenting(parent, transforms))
                            return DragAndDropVisualMode.None;

                        foreach (var trans in transforms)
                            trans.SetParent(parent);

                        if (args.dragAndDropPosition == DragAndDropPosition.BetweenItems)
                        {
                            int insertIndex = args.insertAtIndex;
                            for (int i = transforms.Count - 1; i >= 0; i--)
                            {
                                var transform = transforms[i];
                                insertIndex = GetAdjustedInsertIndex(parent, transform, insertIndex);
                                transform.SetSiblingIndex(insertIndex);
                            }
                        }

                        break;

                    case DragAndDropPosition.OutsideItems:
                        foreach (var trans in transforms)
                        {
                            trans.SetParent(null); // make root when dragged to empty space in treeview
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                Reload();
                SetSelection(transforms.Select(t => t.gameObject.GetInstanceID()).ToList(),
                    TreeViewSelectionOptions.RevealAndFrame);
            }

            return DragAndDropVisualMode.Move;
        }

        int GetAdjustedInsertIndex(Transform parent, Transform transformToInsert, int insertIndex)
        {
            if (transformToInsert.parent == parent && transformToInsert.GetSiblingIndex() < insertIndex)
                return --insertIndex;
            return insertIndex;
        }

        bool IsValidReparenting(Transform parent, List<Transform> transformsToMove)
        {
            if (parent == null)
                return true;

            foreach (var transformToMove in transformsToMove)
            {
                if (transformToMove == parent)
                    return false;

                if (IsHoveredAChildOfDragged(parent, transformToMove))
                    return false;
            }

            return true;
        }


        bool IsHoveredAChildOfDragged(Transform hovered, Transform dragged)
        {
            Transform t = hovered.parent;
            while (t)
            {
                if (t == dragged)
                    return true;
                t = t.parent;
            }

            return false;
        }


        // Returns true if there is an ancestor of transform in the transforms list
        static bool IsDescendantOf(Transform transform, List<Transform> transforms)
        {
            while (transform != null)
            {
                transform = transform.parent;
                if (transforms.Contains(transform))
                    return true;
            }

            return false;
        }

        static void RemoveItemsThatAreDescendantsFromOtherItems(List<Transform> transforms)
        {
            transforms.RemoveAll(t => IsDescendantOf(t, transforms));
        }
    }
    // texture helpers not use.
    class Textures
    {
        static Texture2D m_WhiteTexture;

        /// <summary>
        /// A 1x1 white texture.
        /// </summary>
        /// <remarks>
        /// This texture is only created once and recycled afterward. You shouldn't modify it.
        /// </remarks>
        public static Texture2D whiteTexture
        {
            get
            {
                if (m_WhiteTexture == null)
                {
                    m_WhiteTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false) { name = "White Texture" };
                    m_WhiteTexture.SetPixel(0, 0, Color.white);
                    m_WhiteTexture.Apply();
                }

                return m_WhiteTexture;
            }
        }

        static Texture3D m_WhiteTexture3D;

        /// <summary>
        /// A 1x1x1 white texture.
        /// </summary>
        /// <remarks>
        /// This texture is only created once and recycled afterward. You shouldn't modify it.
        /// </remarks>
        public static Texture3D whiteTexture3D
        {
            get
            {
                if (m_WhiteTexture3D == null)
                {
                    m_WhiteTexture3D = new Texture3D(1, 1, 1, TextureFormat.ARGB32, false)
                    { name = "White Texture 3D" };
                    m_WhiteTexture3D.SetPixels(new Color[] { Color.white });
                    m_WhiteTexture3D.Apply();
                }

                return m_WhiteTexture3D;
            }
        }

        static Texture2D m_BlackTexture;

        /// <summary>
        /// A 1x1 black texture.
        /// </summary>
        /// <remarks>
        /// This texture is only created once and recycled afterward. You shouldn't modify it.
        /// </remarks>
        public static Texture2D blackTexture
        {
            get
            {
                if (m_BlackTexture == null)
                {
                    m_BlackTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false) { name = "Black Texture" };
                    m_BlackTexture.SetPixel(0, 0, Color.black);
                    m_BlackTexture.Apply();
                }

                return m_BlackTexture;
            }
        }

        static Texture3D m_BlackTexture3D;

        /// <summary>
        /// A 1x1x1 black texture.
        /// </summary>
        /// <remarks>
        /// This texture is only created once and recycled afterward. You shouldn't modify it.
        /// </remarks>
        public static Texture3D blackTexture3D
        {
            get
            {
                if (m_BlackTexture3D == null)
                {
                    m_BlackTexture3D = new Texture3D(1, 1, 1, TextureFormat.ARGB32, false)
                    { name = "Black Texture 3D" };
                    m_BlackTexture3D.SetPixels(new Color[] { Color.black });
                    m_BlackTexture3D.Apply();
                }

                return m_BlackTexture3D;
            }
        }

        static Texture2D m_TransparentTexture;

        /// <summary>
        /// A 1x1 transparent texture.
        /// </summary>
        /// <remarks>
        /// This texture is only created once and recycled afterward. You shouldn't modify it.
        /// </remarks>
        public static Texture2D transparentTexture
        {
            get
            {
                if (m_TransparentTexture == null)
                {
                    m_TransparentTexture = new Texture2D(1, 1, TextureFormat.ARGB32, false)
                    { name = "Transparent Texture" };
                    m_TransparentTexture.SetPixel(0, 0, Color.clear);
                    m_TransparentTexture.Apply();
                }

                return m_TransparentTexture;
            }
        }

        static Texture3D m_TransparentTexture3D;

        /// <summary>
        /// A 1x1x1 transparent texture.
        /// </summary>
        /// <remarks>
        /// This texture is only created once and recycled afterward. You shouldn't modify it.
        /// </remarks>
        public static Texture3D transparentTexture3D
        {
            get
            {
                if (m_TransparentTexture3D == null)
                {
                    m_TransparentTexture3D = new Texture3D(1, 1, 1, TextureFormat.ARGB32, false)
                    { name = "Transparent Texture 3D" };
                    m_TransparentTexture3D.SetPixels(new Color[] { Color.clear });
                    m_TransparentTexture3D.Apply();
                }

                return m_TransparentTexture3D;
            }
        }
    }
    // popup window context for size input.
    public class SizePopup : PopupWindowContent
    {    //Search results can be filtered by specifying a series of properties that sounds should match. 
         //In other words, using the filter parameter you can specify the value that certain sound fields should have in order to be considered valid search results. 
         //Filters are defined with a syntax like filter=fieldname:value fieldname:value (that is the Solr filter syntax). 
         //Use double quotes for multi-word queries (filter=fieldname:"val ue"). Filter names can be any of the following:
        public EditorWindow _caller;
        private int x;
        private int y;

        public delegate void OnReceive(Vector2 v2);


        private EditorWindow parent;
        private OnReceive onReceive;
        public bool isInitialized;

        //float width = 350;
        //float height = 200;

        public SizePopup()
        {
            //this.parent = parent;
            //this.onReceive = onReceive;
            isInitialized = true;
        }

        Vector2 scrollPosition;
        public override Vector2 GetWindowSize()
        {
            return (_caller) ? new Vector2(250, 450) : Vector2.zero;
        }

        public override void OnGUI(Rect rect)
        {
            if (_caller)
            {
                EditorGUIUtility.labelWidth = 50;
                using (EditorHelper.Horizontal.Do())
                {
                    GUILayout.Label("Add New viewport size", EditorStyles.whiteLargeLabel);
                    if (GUILayout.Button("X", GUILayout.Width(40)))
                        OnClose();
                }

                using (EditorHelper.Horizontal.Do())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("Landscape", EditorStyles.miniLabel);
                        DrawPresetButton(800, 600);
                        DrawPresetButton(1280, 720);
                        DrawPresetButton(1600, 900);
                        DrawPresetButton(1920, 1080);
                        DrawPresetButton(2560, 1440);
                        DrawPresetButton(3840, 2160);
                    }

                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("Portrait", EditorStyles.miniLabel);
                        DrawPresetButton(600, 800);
                        DrawPresetButton(720, 1280);
                        DrawPresetButton(900, 1600);
                        DrawPresetButton(1080, 1920);
                        DrawPresetButton(1440, 2560);
                        DrawPresetButton(2160, 3840);
                    }

                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField("POT", EditorStyles.miniLabel);
                        DrawPresetButton(128, 128);
                        DrawPresetButton(256, 256);
                        DrawPresetButton(512, 512);
                        DrawPresetButton(1024, 1024);
                        DrawPresetButton(2048, 2048);
                        DrawPresetButton(4096, 4096);
                    }
                }

                using (EditorHelper.Horizontal.Do())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        x = EditorGUILayout.IntSlider("Width", x, 128, 4096);
                        y = EditorGUILayout.IntSlider("Height", y, 128, 4096);
                    }

                    if (GUILayout.Button("Add", GUILayout.Width(80), GUILayout.ExpandHeight(true)))
                        if (onReceive != null)
                            onReceive.Invoke(new Vector2(x, y));
                }
            }
        }

        public override void OnOpen()
        {
            _caller = EditorWindow.GetWindow<EditorWindow>();
        }
        public override void OnClose()
        {
        }

        void DrawPresetButton(int width, int height)
        {

            if (GUILayout.Button(string.Format("{0}x{1}", width, height), EditorStyles.miniButton))
            {
                this.x = width;
                this.y = height;
            }
        }
    }
    // popup window for additional inputs.
    class PopupWindow : EditorWindow
    {
        private int x;
        private int y;

        public delegate void OnReceive(Vector2 v2);

        public delegate void OnClose();

        private EditorWindow parent;
        private OnReceive onReceive;
        private OnClose onClose;
        public bool isInitialized;

        float width = 350;
        float height = 200;

        public void Init(EditorWindow parent, OnReceive onReceive, OnClose onClose)
        {
            this.parent = parent;
            this.onReceive = onReceive;
            this.onClose = onClose;
            isInitialized = true;
        }

        void OnFocus()
        {
        }

        void OnLostFocus()
        {
            CloseWindow();
        }

        void OnGUI()
        {
            EditorGUIUtility.labelWidth = 50;
            ValidateWindow();

            position = new Rect(parent.position.x + (parent.position.width - width) / 2,
                parent.position.y + (parent.position.height - height) / 2, width, height);
            using (EditorHelper.Horizontal.Do())
            {
                GUILayout.Label("Add New viewport size", EditorStyles.whiteLargeLabel);
                if (GUILayout.Button("X", GUILayout.Width(40)))
                    CloseWindow();
            }

            using (EditorHelper.Horizontal.Do())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField("Landscape", EditorStyles.miniLabel);
                    DrawPresetButton(800, 600);
                    DrawPresetButton(1280, 720);
                    DrawPresetButton(1600, 900);
                    DrawPresetButton(1920, 1080);
                    DrawPresetButton(2560, 1440);
                    DrawPresetButton(3840, 2160);
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField("Portrait", EditorStyles.miniLabel);
                    DrawPresetButton(600, 800);
                    DrawPresetButton(720, 1280);
                    DrawPresetButton(900, 1600);
                    DrawPresetButton(1080, 1920);
                    DrawPresetButton(1440, 2560);
                    DrawPresetButton(2160, 3840);
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField("POT", EditorStyles.miniLabel);
                    DrawPresetButton(128, 128);
                    DrawPresetButton(256, 256);
                    DrawPresetButton(512, 512);
                    DrawPresetButton(1024, 1024);
                    DrawPresetButton(2048, 2048);
                    DrawPresetButton(4096, 4096);
                }
            }

            using (EditorHelper.Horizontal.Do())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    x = EditorGUILayout.IntSlider("Width", x, 128, 4096);
                    y = EditorGUILayout.IntSlider("Height", y, 128, 4096);
                }

                if (GUILayout.Button("Add", GUILayout.Width(80), GUILayout.ExpandHeight(true)))
                    if (onReceive != null)
                        onReceive.Invoke(new Vector2(x, y));
            }
        }

        void DrawPresetButton(int width, int height)
        {

            if (GUILayout.Button(string.Format("{0}x{1}", width, height), EditorStyles.miniButton))
            {
                this.x = width;
                this.y = height;
            }
        }

        void ValidateWindow()
        {
            bool isParentFocused = EditorWindow.focusedWindow == parent;
            bool isThisFocused = EditorWindow.focusedWindow == this;
            if (!isParentFocused && !isThisFocused) CloseWindow();
        }

        void CloseWindow()
        {
            if (onClose != null) onClose.Invoke();
            this.Close();
        }

        void OnInspectorUpdate()
        {
            Repaint();
        }
    }
    // draw text on viewport directly.
    static class Notice
    {
        private static StringBuilder _sb;
        private static GUIContent _log;
        private static GUIStyle style;
        private static float timer = 0;

        static Notice()
        {
            _sb = new StringBuilder();
            style = new GUIStyle();
            style.alignment = TextAnchor.UpperLeft;
            style.richText = false;
            style.fontSize = 9;
        }

        public static void Log(object message, bool debugOutput = false)
        {
            _sb.Append(message);
            _sb.Append("\n");
            timer = 5;
            if (debugOutput) Debug.Log(message);
        }

        public static void OnGUI(Rect r)
        {
            style.normal.textColor = Color.white * timer;
            _log = new GUIContent(_sb.ToString());
            var infoSize = style.CalcSize(_log);
            Rect area = new Rect(r.x + 4, r.y, infoSize.x, infoSize.y);
            EditorGUI.DropShadowLabel(area, _log, style);
            timer -= 0.01f;
            if (timer < 0)
            {
                timer = 0;
                _sb.Length = 0;
            }
        }
    }
    // simple shortcut manager.
    class Shortcuts
    {
        static StringBuilder sb = new StringBuilder();
        static Dictionary<KeyCode, UnityAction> shortcutDic = new Dictionary<KeyCode, UnityAction>();

        public static void AddBlank(GUIContent desc)
        {
            sb.AppendFormat("{0}", desc.text);
            sb.AppendLine();
        }

        public static void Add(KeyCode input, GUIContent desc, UnityAction action)
        {
            shortcutDic.Add(input, action);
            sb.AppendFormat("{0} - {1}", input.ToString(), desc.text);
            sb.AppendLine();
        }

        public static void Clear()
        {
            shortcutDic.Clear();
            sb.Length = 0;
        }

        public static void ProcessInput(KeyCode input)
        {
            if (shortcutDic.ContainsKey(input))
            {
                if (shortcutDic[input] != null)
                {
                    shortcutDic[input].Invoke();
                }
            }
        }

        public static string Print()
        {
            return sb.ToString();
        }
    }

    public class TransformModifier
    {
        public string name = string.Empty;
        public Transform transform;
        public Transform transformPair;
        // 1차 자식
        public List<Transform> children;
        private bool isDontAppectChildren;
        public bool isSymmetrical;

        private Vector3 orgPosition = Vector3.zero;
        private Quaternion orgRotation = Quaternion.identity;
        private Vector3 orgScale = Vector3.one;

        private Vector3 offset = Vector3.zero;
        private Quaternion rotation = Quaternion.identity;
        private Vector3 scale = Vector3.one;
        internal object m_TargetPath;

        public TransformModifier(Transform root, string boneName, bool isPath, bool isSymmetrical = false, bool isDontAppectChildren = false)
        {
            this.isSymmetrical = isSymmetrical;
            this.isDontAppectChildren = isDontAppectChildren;
            if (isPath)
            {
                transform = GetHierachyTarget(root, boneName);
            }
            else
            {
                transform = FindTransformRecursive(root, boneName);
            }
            this.name = Path.GetFileName(boneName);

            if (transform)
            {
                orgPosition = transform.localPosition;
                orgRotation = transform.localRotation;
                orgScale = transform.localScale;
            }
            if (isSymmetrical)
            {
                string leftIdentifier = " L ";
                string RightIdentifier = " R ";
                bool isLeft = boneName.Contains(leftIdentifier);
                name = name.Replace(isLeft ? leftIdentifier : RightIdentifier, "");
                transformPair = FindTransformRecursive(root, boneName.Replace(isLeft ? leftIdentifier : RightIdentifier, isLeft ? RightIdentifier : leftIdentifier));
            }
        }

        private Transform GetHierachyTarget(Transform root, string relativePath)
        {
            //루트 트랜스폼 오브젝트 상태 저장 및 강제 활성화
            var rootActive = root.gameObject.activeInHierarchy;
            root.gameObject.SetActive(true);
            string path = relativePath.Replace(root.name + "/","");

            Debug.Log(path);
            Transform objToFind = root.Find(path); // GameObject.Find 는 비활성화된 오브젝트에 적용불가
            //GameObject go = GameObject.Find(path);
            if (objToFind)
            {
                root.gameObject.SetActive(rootActive);
                return objToFind.transform;
            }
            else return root.transform;
        }

        public void SetModification(Vector3 positionOffset, Quaternion rotation, Vector3 scale)
        {
            this.offset = positionOffset;
            this.rotation = rotation;
            this.scale = scale;
        }

        public void OnGUI(bool pos, bool rot, bool scale)
        {
            GUILayout.Label(name, EditorStyles.boldLabel);
            if (pos)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Offset", EditorStyles.boldLabel);
                    if (GUILayout.Button("Reset")) this.offset = Vector3.zero;
                }
                float x = offset.x;
                float y = offset.y;
                float z = offset.z;
                x = EditorGUILayout.Slider("X", x, -1f, 1f);
                y = EditorGUILayout.Slider("Y", y, -1f, 1f);
                z = EditorGUILayout.Slider("Z", z, -1f, 1f);
                this.offset = new Vector3(x, y, z);
            }
            if (rot)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Rotation", EditorStyles.boldLabel);
                    if (GUILayout.Button("Reset")) this.rotation = Quaternion.identity;
                }
                float x = rotation.eulerAngles.x;
                float y = rotation.eulerAngles.y;
                float z = rotation.eulerAngles.z;
                x = EditorGUILayout.Slider("X", x, -180f, 180f);
                y = EditorGUILayout.Slider("Y", y, -180f, 180f);
                z = EditorGUILayout.Slider("Z", z, -180f, 180f);
                this.rotation = Quaternion.Euler(x, y, z);
            }
            if (scale)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Scale", EditorStyles.boldLabel);
                    if (GUILayout.Button("Reset")) this.scale = Vector3.one;
                }
                float x = this.scale.x;
                float y = this.scale.y;
                float z = this.scale.z;
                x = EditorGUILayout.Slider("X", x, 0f, 2f);
                y = EditorGUILayout.Slider("Y", y, 0f, 2f);
                z = EditorGUILayout.Slider("Z", z, 0f, 2f);
                this.scale = new Vector3(x, y, z);
            }
            Apply();
        }

        public void Apply()
        {
            if (transform)
            {
                transform.localPosition = orgPosition + offset;
                transform.localRotation = orgRotation * rotation;
                transform.localScale = Vector3.Scale(orgScale, scale);
            }
            if (isSymmetrical)
            {
                transformPair.localPosition = orgPosition + offset;
                transformPair.localRotation = orgRotation * rotation;
                transformPair.localScale = Vector3.Scale(orgScale, scale);
            }
            if (isDontAppectChildren)
            {

            }
        }

        public void Reset()
        {
            offset = Vector3.zero;
            rotation = Quaternion.identity;
            scale = Vector3.one;
            Apply();
        }

        // Recursive function to find a Transform by name in the hierarchy
        Transform FindTransformRecursive(Transform parent, string name)
        {
            Transform result = null;

            // Check if the current Transform has the desired name
            if (parent.name == name)
            {
                result = parent;
            }
            else
            {
                // Iterate through child Transforms and recursively search
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform child = parent.GetChild(i);
                    Transform foundInChildren = FindTransformRecursive(child, name);

                    // If found in children, return the result
                    if (foundInChildren != null)
                    {
                        result = foundInChildren;
                        break;
                    }
                }
            }

            return result;
        }
    }

    // apply animationclip to multiple actor
    public class AnimationPlayer
    {

        public  string[] _modelRootHierachy;
        public List<TransformModifier> modifierList = new List<TransformModifier>();

        public class Actor
        {
            public bool enabled;
            public GameObject prefab;
            public GameObject instance;
            public Animator animator;
            public Bounds bounds;

            public bool isSceneObject
            {
                get { return prefab == null; }
            }
            public string name
            {
                get { return isSceneObject ? instance.name : prefab.name; }
            }
            public Actor(GameObject src, bool sceneObject)
            {
                this.enabled = true;
                if (sceneObject)
                {
                    this.instance = src;
                }
                else
                {
                    this.prefab = src;
                    this.instance = (GameObject)PrefabUtility.InstantiatePrefab((prefab));
                    if (!instance)
                    {
                        Debug.Log(string.Format("Can't instantiate : {0}", src.name));
                        return;
                    }
                    this.instance.name = prefab.name + "(Actor)";
                }
                Animator animator = instance.GetComponent<Animator>();
                if (animator)
                {
                    this.animator = animator;
                }
                var renderers = instance.GetComponentsInChildren<Renderer>().ToList();
                foreach (var renderer in renderers)
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

        }

        public class ClipInfo
        {
            public AnimationClip clip;
            public bool enabled;
            //public int loopTimes;
            StringBuilder sb = new StringBuilder();

            public ClipInfo(AnimationClip clip)
            {
                this.clip = clip;
                //sb.AppendFormat("Name : {0}", clip.name);
                //sb.AppendLine();
                //sb.AppendFormat("Local Bounds : {0}", clip.localBounds.ToString());
                //sb.AppendLine();
                //sb.AppendFormat("Events : {0}", clip.events.ToString());
                //sb.AppendLine();
                sb.AppendFormat("FrameRate : {0}", clip.frameRate.ToString());
                sb.AppendLine();
                //sb.AppendFormat("Human Motion : {0}", clip.humanMotion.ToString());
                //sb.AppendLine();
                //sb.AppendFormat("Legacy : {0}", clip.legacy.ToString());
                //sb.AppendLine();
                sb.AppendFormat("Length : {0}", clip.length.ToString("0.00"));
                //sb.AppendLine();
                //sb.AppendFormat("WrapMode : {0}", clip.wrapMode.ToString());
                //sb.AppendLine();
                //sb.AppendFormat("Apparent Speed : {0}", clip.apparentSpeed.ToString());
                //sb.AppendLine();
                //sb.AppendFormat("Average Angular Speed : {0}", clip.averageAngularSpeed.ToString());
                //sb.AppendLine();
                //sb.AppendFormat("Average Duration : {0}", clip.averageDuration.ToString());
                //sb.AppendLine();
                //sb.AppendFormat("Average Speed : {0}", clip.averageSpeed.ToString());
            }

            public string Print()
            {
                return sb.ToString();
            }
        }

        internal UnityEditorInternal.ReorderableList reorderableActorList;
        internal UnityEditorInternal.ReorderableList reorderableClipList;
        internal List<Actor> actorList = new List<Actor>();
        internal List<AnimationClip> playList = new List<AnimationClip>();
        internal List<ClipInfo> clipInfoList = new List<ClipInfo>();
        private int current;
        internal double time = 0.0f;
        internal float timeSpeed = 1.0f;
        private bool _isOptimized { get; set; }
        internal bool isPlayable { get { return actorList.Count > 0 && clipInfoList.Count > 0 && playList.Count > 0; } }
        internal bool isPlaying { get; set; }
        internal bool isLooping { get; set; }
        private bool _showEvent { get; set; }
        private int _actorRow = 4;
        private int _actorDistance = 1;
        internal AnimationClip _currentClip { get { return playList[0]; } }
        internal UnityEvent onStopPlaying = new UnityEvent();
        internal string modifierTargetPath = string.Empty;

        internal ClipInfo currentClipInfo
        {
            get { return clipInfoList.FirstOrDefault(x => x.clip == _currentClip); }
        }
        //Texture aniIcon = EditorGUIUtility.IconContent("Animator Icon").image;

        public AnimationPlayer()
        {
            InitActorList();
            InitClipList();
        }

        private static string[] BuildHierachialPath(Transform root)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>();
            List<string> list = new List<string>();
            foreach (var tr in all)
            {
                list.Add(GetTransformPath(tr));
            }
            return list.ToArray();
        }
        private static string GetTransformPath(Transform tr)
        {
            Transform root = tr;
            while (root.parent != null)
            {
                root = root.parent;
            }
            root = root.parent;
            return AnimationUtility.CalculateTransformPath(tr, root); //지정한 루트로부터의 path를 생성해줌
        }

        public void Dispose()
        {
            foreach (var actor in actorList.ToArray())
            {
                RemoveActor(actor);
            }
        }

        public void TogglePlay()
        {
            isPlaying = !isPlaying;
            if (isPlaying) Play();
        }

        private void InitActorList()
        {
            actorList = new List<Actor>();
            reorderableActorList = new UnityEditorInternal.ReorderableList(actorList, typeof(GameObject), true, true, false, false);
            //fields
            reorderableActorList.showDefaultBackground = false;
            reorderableActorList.headerHeight = 20;
            reorderableActorList.elementHeight = 18;
            reorderableActorList.footerHeight = 40;
            //draw callback
            reorderableActorList.drawHeaderCallback = (position) =>
            {
                var btn30 = position.width * 0.3333f;
                position.width = btn30;
                using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                {
                    if (GUI.Button(position, "Add", EditorStyles.miniButtonLeft))
                    {
                        reorderableActorList.onAddDropdownCallback.Invoke(position, reorderableActorList);
                    }
                }

                position.x += position.width;
                position.width = btn30;
                using (new EditorGUI.DisabledScope(reorderableActorList.index < 0))
                {
                    if (GUI.Button(position, "Remove", EditorStyles.miniButtonMid))
                    {
                        reorderableActorList.onRemoveCallback(reorderableActorList);
                    }
                }

                position.x += position.width;
                position.width = btn30;
                using (new EditorGUI.DisabledScope(actorList.Count == 0))
                {
                    if (GUI.Button(position, "Clear", EditorStyles.miniButtonRight))
                    {
                        foreach (var actor in actorList.ToArray())
                        {
                            RemoveActor(actor);
                        }
                    }
                }
            };
            reorderableActorList.drawElementCallback = (position, index, isActive, isFocused) =>
            {
                if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && position.Contains(Event.current.mousePosition))
                {
                    Selection.activeGameObject = actorList[index].instance;
                    SceneView.lastActiveSceneView.pivot = actorList[index].instance.transform.position + actorList[index].bounds.center;
                }

                float rectWidth = position.width;
                float rectHeight = position.height;
                float tglWidth = 15;
                float btnWidth = 55;
                position.width = tglWidth;
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    actorList[index].enabled = EditorGUI.Toggle(position, actorList[index].enabled);
                    if (check.changed)
                    {
                    }
                }

                position.x += position.width;
                position.width = rectWidth - btnWidth - tglWidth;
                EditorGUI.LabelField(position, actorList[index].name, EditorStyles.miniBoldLabel);
                var style = new GUIStyle(EditorStyles.miniLabel);
                style.alignment = TextAnchor.MiddleRight;
                style.normal.textColor = Color.gray;
                bool animatorExist = actorList[index].animator != null;
                if (animatorExist)
                    EditorGUI.LabelField(position, actorList[index].animator.isHuman ? "Humanoid" : "Generic", style);
                position.x += position.width;
                position.width = btnWidth;
                position.height = 16;
                if (animatorExist)
                {
                    if (GUI.Button(position, "GetClips", EditorStyles.miniButton))
                    {
                        InitAnimatorAndClips(actorList[index].animator);
                    }
                }
            };

            reorderableActorList.drawFooterCallback = position =>
            {

                var start = position;
                var btn50 = position.width * 0.5f;
                position.height *= 0.5f;
                var labelwidth = 60;
                EditorGUIUtility.labelWidth = labelwidth;
                position.width = btn50;
                var controlPos = EditorGUI.PrefixLabel(position, new GUIContent(string.Format(" Rows : {0}", _actorRow)), EditorStyles.miniLabel);
                _actorRow = (int)GUI.HorizontalSlider(controlPos, _actorRow, 1, 8);
                position.x += position.width;
                controlPos = EditorGUI.PrefixLabel(position, new GUIContent(string.Format(" Dist : {0}", _actorDistance)), EditorStyles.miniLabel);
                _actorDistance = (int)GUI.HorizontalSlider(controlPos, _actorDistance, 1, 8);
                EditorGUIUtility.labelWidth = labelwidth;
                position.x = start.x;
                position.y += position.height;
                position.width = btn50;
                if (GUI.Button(position, "Grid", EditorStyles.miniButtonLeft))
                {
                    SetActorPosition(true);
                }
                position.x += position.width;
                if (GUI.Button(position, "Reset", EditorStyles.miniButtonRight))
                {
                    SetActorPosition(false);
                }
                position.x += position.width;
            };
            //btn callback
            reorderableActorList.onAddDropdownCallback = (buttonRect, list) =>
            {
                var selection = Selection.gameObjects;
                foreach (var go in selection)
                {
                    var root = go.transform.root;
                    if (root)
                    {
                        if (!AssetDatabase.Contains(root.gameObject) && !(Enumerable.Any(actorList, x => x.instance == root.gameObject)))
                        {
                            this.actorList.Add(new Actor(root.gameObject, true));
                        }
                    }
                }
            };
            reorderableActorList.onRemoveCallback = (list) =>
            {
                reorderableActorList.index = Mathf.Clamp(reorderableActorList.index, 0, reorderableActorList.count - 1);
                if (actorList.Count > 0)
                {
                    var actor = actorList[reorderableActorList.index];
                    RemoveActor(actor);
                }

                reorderableActorList.index = Mathf.Clamp(reorderableActorList.index, 0, reorderableActorList.count - 1);
            };
            reorderableActorList.onChangedCallback = list => { };
        }

        private void InitClipList()
        {
            clipInfoList = new List<ClipInfo>();
            reorderableClipList = new UnityEditorInternal.ReorderableList(clipInfoList, typeof(ClipInfo), true, true, false, false);
            //fields
            reorderableClipList.showDefaultBackground = false;
            reorderableClipList.headerHeight = 20;
            reorderableClipList.elementHeight = 18;
            reorderableClipList.footerHeight = 20;
            //draw callback
            reorderableClipList.drawHeaderCallback = (position) =>
            {
                Event evt = Event.current;
                var btn30 = position.width * 0.3333f;
                position.width = btn30;
                if (GUI.Button(position, "Add", EditorStyles.miniButtonLeft))
                {
                    reorderableClipList.onAddDropdownCallback.Invoke(position, reorderableClipList);
                }

                position.x += position.width;
                position.width = btn30;
                using (new EditorGUI.DisabledScope(reorderableClipList.index < 0))
                {
                    if (GUI.Button(position, "Remove", EditorStyles.miniButtonMid))
                    {
                        reorderableClipList.onRemoveCallback(reorderableClipList);
                    }
                }
                position.x += position.width;
                position.width = btn30;
                using (new EditorGUI.DisabledScope(clipInfoList.Count == 0))
                {
                    if (GUI.Button(position, "Clear", EditorStyles.miniButtonRight))
                    {
                        clipInfoList.Clear();
                    }
                }
                string commandName = Event.current.commandName;
                if (commandName == "ObjectSelectorUpdated")
                {
                    var clip = EditorGUIUtility.GetObjectPickerObject() as AnimationClip;
                    if (clip)
                    {
                        if (!Enumerable.Any(clipInfoList, x => x.clip == clip))
                        {
                            clipInfoList.Add(new ClipInfo(clip));
                            RefreshPlayList();
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                        }
                    }
                }
                else if (commandName == "ObjectSelectorClosed")
                {
                    var clip = EditorGUIUtility.GetObjectPickerObject() as AnimationClip;
                    if (clip)
                    {
                        if (!Enumerable.Any(clipInfoList, x => x.clip == clip))
                        {
                            clipInfoList.Add(new ClipInfo(clip));
                            RefreshPlayList();
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                        }
                    }
                }

            };
            reorderableClipList.drawElementCallback = (position, index, isActive, isFocused) =>
            {
                if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && position.Contains(Event.current.mousePosition))
                {
                    PlayInstant(index);
                }
                float rectWidth = position.width;
                float rectHeight = position.height;
                float tglWidth = 15;
                float btnWidth = 55;
                position.width = tglWidth;
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    clipInfoList[index].enabled = EditorGUI.Toggle(position, clipInfoList[index].enabled);
                    if (check.changed)
                    {
                        RefreshPlayList();
                    }
                }
                position.x += position.width;
                position.width = rectWidth - btnWidth - tglWidth;
                bool playing = isPlayable && playList[0] == clipInfoList[index].clip;
                var style0 = new GUIStyle(EditorStyles.miniLabel);
                style0.normal.textColor = playing ? Color.white : Color.gray;
                EditorGUI.LabelField(position, string.Format("{0}", clipInfoList[index].clip.name), style0);
                var style1 = new GUIStyle(EditorStyles.miniLabel);
                style1.alignment = TextAnchor.MiddleRight;
                style1.normal.textColor = Color.gray;
                EditorGUI.LabelField(position, clipInfoList[index].clip.humanMotion ? "HumanMotion" : "Generic", style1);
                position.x += position.width;
                position.width = btnWidth;
                position.height = 16;
                if (GUI.Button(position, "Select", EditorStyles.miniButton))
                {
                    Selection.activeObject = clipInfoList[index].clip;
                }
            };
            reorderableClipList.drawFooterCallback = position =>
            {
                //var btn20 = position.width * 0.2f;
                //var btn25 = position.width * 0.25f;
                //var btn30 = position.width * 0.3f;
                var btn50 = position.width * 0.5f;
                position.width = btn50;
                if (GUI.Button(position, "Check All", EditorStyles.miniButtonLeft))
                {
                    foreach (var info in clipInfoList)
                    {
                        info.enabled = true;
                    }

                    RefreshPlayList();
                }

                position.x += position.width;
                position.width = btn50;
                if (GUI.Button(position, "Uncheck All", EditorStyles.miniButtonRight))
                {
                    foreach (var info in clipInfoList)
                    {
                        info.enabled = false;
                    }

                    RefreshPlayList();
                }

                position.x += position.width;
            };
            //btn callback
            reorderableClipList.onAddDropdownCallback = (buttonRect, list) =>
            {
                int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                EditorGUIUtility.ShowObjectPicker<AnimationClip>(null, false, string.Empty, currentPickerWindow);
            };
            reorderableClipList.onRemoveCallback = (list) =>
            {
                reorderableClipList.index = Mathf.Clamp(reorderableClipList.index, 0, reorderableClipList.count - 1);
                if (clipInfoList.Count > 0)
                {
                    clipInfoList.RemoveAt(reorderableClipList.index);
                    RefreshPlayList();
                }
                reorderableClipList.index = Mathf.Clamp(reorderableClipList.index, 0, reorderableClipList.count - 1);
            };
            reorderableClipList.onChangedCallback = list => { RefreshPlayList(); };
        }

        public void PlayInstant(int index)
        {
            foreach (var info in clipInfoList)
            {
                info.enabled = false;
            }

            clipInfoList[index].enabled = true;
            RefreshPlayList();
            Play();
        }

        public void PlayInstant(AnimationClip clip)
        {
            if (clipInfoList.Where(x => x.clip == clip).Any())
            {
                foreach (var info in clipInfoList)
                {
                    info.enabled = false;
                }
                var index = clipInfoList.IndexOf(clipInfoList.Where(x => x.clip == clip).FirstOrDefault());
                clipInfoList[index].enabled = true;
                RefreshPlayList();
                Play();
            }
        }

        void InitAnimatorAndClips(Animator animator)
        {
            foreach (var actor in actorList.ToArray())
            {
                if (animator)
                {
                    _isOptimized = !animator.hasTransformHierarchy;
                    //스킨드메쉬 초기화 및 애니메이션 가능하게 디옵티마이즈.
                    //DeOptimizeObject();
                    var clips = AnimationUtility.GetAnimationClips(actor.instance);
                    foreach (var clip in clips)
                    {
                        if (Enumerable.Any(clipInfoList, x => x.clip == clip)) continue;
                        clipInfoList.Add(new ClipInfo(clip));
                    }
                }
            }
            RefreshPlayList();
        }

        public void AddActor(GameObject go, bool isSceneObject, bool collectClip = true)
        {
            if (actorList.Any(x => (isSceneObject ? x.instance : x.prefab) == go)) return;
            var actor = new Actor(go, isSceneObject);
            actorList.Add(actor);
            if (collectClip)
            {
                InitAnimatorAndClips(actor.animator);
            }
            _modelRootHierachy = BuildHierachialPath(actorList[0].instance.transform);
        }

        public void RemoveActor(Actor actor)
        {
            if (!actor.isSceneObject) GameObject.DestroyImmediate(actor.instance);
            actorList.Remove(actor);
        }

        public void SetActorPosition(bool grid)
        {
            for (int i = 0; i < actorList.Count; i++)
            {
                if (grid)
                {
                    var row = i / _actorRow;
                    var column = i - (row * _actorRow);
                    var xpos = column * _actorDistance;
                    var zpos = row * _actorDistance;
                    actorList[i].instance.transform.position = new Vector3(-xpos, 0, -zpos);
                }
                else
                {
                    actorList[i].instance.transform.position = Vector3.zero;
                }
            }
        }

        public void AddClip(AnimationClip clip)
        {
            var clips = clipInfoList.Select(x => x.clip).ToList();
            if (clips.Contains(clip)) return;
            clipInfoList.Add(new ClipInfo(clip));
            RefreshPlayList();
        }

        public void AddModifier(string name)
        {
            modifierList.Add(new TransformModifier(actorList[0].instance.transform, name, true));
        }

        public void AddModifier(TransformModifier modifier)
        {
            modifierList.Add(modifier);
        }

        private static void ShowMenu<T>(T selected, string[] itemNames, T[] items, GenericMenu.MenuFunction2 OnSelected)
        {
            // create the menu and add items to it
            GenericMenu menu = new GenericMenu();
            for (int i = 0; i < itemNames.Length; i++)
            {
                menu.AddItem(new GUIContent(itemNames[i]), selected.Equals(items[i]), OnSelected, items[i]);
            }
            menu.ShowAsContext();
        }
        public void AddModifierHandler()
        {
            var path = string.Empty;
            ShowMenu(path, _modelRootHierachy, _modelRootHierachy, (x) =>
            {
                path = (string)x;
                AddModifier(path);
            });
        }
        void ToggleAnimationMode()
        {
            if (AnimationMode.InAnimationMode())
                AnimationMode.StopAnimationMode();
            else
                AnimationMode.StartAnimationMode();
        }

        public void Update(double delta)
        {
            if (!isPlayable)
            {
                Stop();
                return;
            }

            if (actorList.Count > 0)
            {
                if (AnimationMode.InAnimationMode())
                {
                    AnimationMode.BeginSampling();
                    for (var i = 0; i < actorList.Count; i++)
                    {
                        var animated = actorList[i];
                        if (animated.enabled)
                        {
                            AnimationMode.SampleAnimationClip(animated.instance, _currentClip, (float)time);
                            foreach (var modifier in modifierList)
                            {
                                modifier.Apply();
                            }
                        }
                    }
                    AnimationMode.EndSampling();

                    //_currentClip.SampleAnimation(animatedObject, (float) time);
                    time += isPlaying ? delta * timeSpeed : 0;

                    if (time > _currentClip.length)
                    {
                        time = 0.0f;
                        if (isLooping)
                        {
                            //remove it and add it back to the end
                            AnimationClip ac = _currentClip;
                            playList.Remove(_currentClip);
                            playList.Add(ac);
                        }
                        else
                        {
                            //uncheck it and then remove it
                            clipInfoList.FirstOrDefault(x => x.clip == _currentClip).enabled = false;
                            playList.Remove(_currentClip);
                        }
                    }
                }
            }
        }

        public void OnGUI_Control()
        {
            //if (animInfoList.Count < 1) return;
            if (isPlayable)
            {
                //var ect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight * 1.1f,
                //    EditorStyles.label);
                //using (new GUILayout.AreaScope(ect))
                //{
                //    //GUILayout.Label(animatedObject.name, "LODSliderTextSelected");
                //    //GUILayout.FlexibleSpace();
                //    GUILayout.Label(string.Format("Play Speed : {0}", timeSpeed.ToString("0.0")),
                //        "LODSliderTextSelected");
                //}
                GUILayout.Space(20);
                var progressRect =
                    EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight * 1.1f, GUIStyle.none);
                progressRect = new RectOffset(16, 16, 0, 0).Remove(progressRect);
                time = GUI.HorizontalSlider(progressRect, (float)time, 0, GetCurrentClipLength(), GUIStyle.none,
                    GUIStyle.none);
                float length = GetCurrentClipLength();
                float progress = (float)time / length;
                EditorGUI.ProgressBar(progressRect, progress,
                    string.Format("{0} : {1}s", GetCurrentClipName(), length.ToString("0.00")));

                if (_showEvent)
                {
                    foreach (var animEvent in _currentClip.events)
                    {
                        var timePos = progressRect.x + (progressRect.width * animEvent.time / _currentClip.length);
                        //marker
                        GUIContent marker = GUIContent.none;
                        var markerPos = new Vector2(timePos, progressRect.y);
                        Rect markerRect = new Rect(markerPos, GUIStyle.none.CalcSize(marker));
                        //if (GUI.Button(markerRect, "", "Icon.Event"))
                        //{

                        //}
                        //button
                        GUIContent btn = new GUIContent(animEvent.functionName);
                        var btnPos = new Vector2(timePos, progressRect.y - progressRect.height);
                        Rect btnRect = new Rect(btnPos, GUIStyle.none.CalcSize(btn));
                        if (GUI.Button(btnRect, btn, EditorStyles.miniButton))
                        {

                        }

                    }
                }

                using (var hr = new EditorGUILayout.HorizontalScope())
                {
                    var infoRect = new RectOffset(16, 16, 0, 0).Remove(hr.rect);
                    EditorGUI.DropShadowLabel(infoRect, string.Format("{0}", currentClipInfo.Print()), EditorStyles.miniLabel);
                    GUIStyle style = new GUIStyle(EditorStyles.miniLabel);
                    style.alignment = TextAnchor.MiddleRight;
                    EditorGUI.DropShadowLabel(infoRect, string.Format("Speed : {0}X\n Frame : {1}", timeSpeed.ToString("0.0"), (_currentClip.frameRate * progress * _currentClip.length).ToString("000")), style);
                    GUILayout.FlexibleSpace();

                    //if (GUILayout.Button(isPlaying ? "Pause" : "Play", "ButtonLeft", GUILayout.Width(50),
                    //    GUILayout.Height(30)))
                    //{
                    //    if (isPlaying) Pause();
                    //    else Play();
                    //}

                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        if (AnimationMode.InAnimationMode()) GUI.backgroundColor = Color.red;
                        isPlaying = GUILayout.Toggle(isPlaying, isPlaying ? "Pause" : "Play", "ButtonLeft", GUILayout.Width(50), GUILayout.Height(30));
                        if (check.changed)
                        {
                            if (isPlaying) Play();
                        }
                        GUI.backgroundColor = Color.white;
                    }

                    if (GUILayout.Button("Stop", "ButtonMid", GUILayout.Width(50), GUILayout.Height(30)))
                    {
                        Stop();
                    }

                    isLooping = GUILayout.Toggle(isLooping, "Loop", "ButtonRight", GUILayout.Width(50), GUILayout.Height(30));

                    if (GUILayout.Button("-", "ButtonLeft", GUILayout.Height(30)))
                    {
                        timeSpeed = Mathf.Max(0, (timeSpeed * 10 - 1f) * 0.1f);

                    }

                    if (Mathf.Approximately(timeSpeed, 0.5f)) GUI.backgroundColor = Color.cyan;
                    if (GUILayout.Button("0.5x", "ButtonMid", GUILayout.Height(30)))
                    {
                        timeSpeed = 0.5f;

                    }
                    GUI.backgroundColor = Color.white;

                    if (Mathf.Approximately(timeSpeed, 1.0f)) GUI.backgroundColor = Color.cyan;
                    if (GUILayout.Button("1.0x", "ButtonMid", GUILayout.Height(30)))
                    {
                        timeSpeed = 1.0f;

                    }
                    GUI.backgroundColor = Color.white;

                    if (Mathf.Approximately(timeSpeed, 2.0f)) GUI.backgroundColor = Color.cyan;
                    if (GUILayout.Button("2.0x", "ButtonMid", GUILayout.Height(30)))
                    {
                        timeSpeed = 2.0f;

                    }
                    GUI.backgroundColor = Color.white;

                    if (GUILayout.Button("+", "ButtonRight", GUILayout.Height(30)))
                    {
                        timeSpeed = Mathf.Min(2, (timeSpeed * 10 + 1f) * 0.1f);

                    }

                    _showEvent = GUILayout.Toggle(_showEvent, "Event", "Button", GUILayout.Height(30));

                    GUILayout.FlexibleSpace();
                }
            }
        }

        void RefreshPlayList()
        {
            playList.Clear();
            for (int i = 0; i < clipInfoList.Count; i++)
            {
                {
                    if (clipInfoList[i].enabled)
                    {
                        if (!playList.Contains(clipInfoList[i].clip))
                            playList.Add(clipInfoList[i].clip);
                    }
                    else
                    {
                        playList.Remove(clipInfoList[i].clip);
                    }
                }
            }
        }

        internal void Stop()
        {
            if (AnimationMode.InAnimationMode())
            {
                isPlaying = false;
                time = 0.0f;
                ResetToInitialState();
                AnimationMode.StopAnimationMode();
                if (onStopPlaying != null) onStopPlaying.Invoke();
            }
        }

        internal void DeOptimizeObject()
        {
            for (var i = 0; i < actorList.Count; i++)
            {
                var animated = actorList[i];
                AnimatorUtility.OptimizeTransformHierarchy(animated.instance, new string[] { });
                AnimatorUtility.DeoptimizeTransformHierarchy(animated.instance);
            }
        }

        internal void Play()
        {
            for (var i = 0; i < actorList.Count; i++)
            {
                var animated = actorList[i];
                isPlaying = true;
                if (PrefabUtility.IsPartOfModelPrefab(animated.instance))
                {

                }
                else if (PrefabUtility.IsOutermostPrefabInstanceRoot(animated.instance))
                {
                    PrefabUtility.UnpackPrefabInstance(animated.instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                    AnimatorUtility.DeoptimizeTransformHierarchy(animated.instance);
                }
                if (!AnimationMode.InAnimationMode())
                    AnimationMode.StartAnimationMode();
            }
        }

        internal void Pause()
        {
            isPlaying = false;
        }

        public void ResetToInitialState()
        {
            if (_isOptimized)
            {
                for (var i = 0; i < actorList.Count; i++)
                {
                    var animated = actorList[i];
                    AnimatorUtility.OptimizeTransformHierarchy(animated.instance, null);
                    ReflectionRestoreToBindPose(animated.instance);
                }
            }
        }

        private void ReflectionRestoreToBindPose(Object _target)
        {
            if (_target == null)
                return;
            Type type = Type.GetType("UnityEditor.AvatarSetupTool, UnityEditor");
            if (type != null)
            {
                MethodInfo info = type.GetMethod("SampleBindPose", BindingFlags.Static | BindingFlags.Public);
                if (info != null)
                {
                    info.Invoke(null, new object[] { _target });
                }
            }
        }

        public static void ForceBindPose(string path, bool reimportModel = true)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);

            Type type = Type.GetType("UnityEditor.AvatarSetupTool, UnityEditor");
            if (type != null)
            {
                MethodInfo info = type.GetMethod("SampleBindPose", BindingFlags.Static | BindingFlags.Public);
                if (info != null)
                    info.Invoke(null, new object[] { asset });
            }
            if (!reimportModel) return;

            var modelImporter = AssetImporter.GetAtPath(path) as ModelImporter;
            if (modelImporter != null) modelImporter.SaveAndReimport();
        }

        internal string GetCurrentClipName()
        {
            if (playList != null)
            {
                if (playList.Count > 0)
                {
                    return _currentClip.name;
                }
            }

            return string.Empty;
        }

        internal float GetCurrentClipLength()
        {
            if (playList.Count > 0)
            {
                return _currentClip.length;
            }

            return 0;
        }

        internal float GetPlayListLength()
        {
            float length = 0;
            foreach (var clip in playList)
            {
                length += clip.length;
            }

            return length;
        }
    }
    // simple update checker. wip.
    class Updater
    {
        public class Update
        {
            public string version = string.Empty;
            public string url = string.Empty;
        }

        public static Update update;
        public static string updateCheck = "";
        public static bool outOfDate = false;

        public static int versionNumPrimary = 0;

        //public static string version;
        public static int versionNumSecondary = 9;

        public static string url =
            "https://gist.githubusercontent.com/See1Studios/58d573487d07e11e221a7a499545c1f4/raw/23c3a5ebac03b894fd307c86eedec00b5be05e19/AssetStudioVersion.txt";

        public static string downloadUrl = string.Empty;

        public static void CheckForUpdates()
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(Request(url, SetVersion));
            updateCheck = "Checking for updates...";
        }

        static void SetVersion(string json)
        {
            update = JsonUtility.FromJson<Update>(json);
            if (update != null)
            {
                string[] split = update.version.Split('.');
                int latestMajor = int.Parse(split[0]);
                int latestMinor = int.Parse(split[1]);
                outOfDate = (latestMajor > versionNumPrimary ||
                             latestMajor == versionNumPrimary && latestMinor > versionNumSecondary);
                updateCheck = outOfDate
                    ? "See1View is out of date!\nThe latest version is " + update.version
                    : "See1View is up to date!";
                downloadUrl = update.url;
            }
        }

        internal static IEnumerator Request(string url, Action<string> actionWithText)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SendWebRequest();
                while (!request.isDone)
                {
                    yield return null;
                }

                if (!request.isNetworkError)
                {
                    actionWithText(request.downloadHandler.text);
                }
                else
                {
                    actionWithText("");
                }
            }
        }
    }
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

        public class FoldGroup2 : IDisposable //미완성
        {
            static Dictionary<string, AnimBoolS> dict = new Dictionary<string, AnimBoolS>();
            private static string current;

            public static FoldGroup2 Do(string label, bool initValue)
            {
                if (!dict.ContainsKey(label)) dict.Add(label, new AnimBoolS(initValue));
                current = label;
                dict[label].target = EditorHelper.Foldout(dict[label].target, label);
                if ((double)dict[current].faded == 0.0 || (double)dict[current].faded == 1.0)
                {
                    EditorGUILayout.BeginFadeGroup(dict[label].faded);
                }

                return new FoldGroup2(label, dict[label].faded);
            }

            public FoldGroup2(string label, float value)
            {
                current = label;
                if ((double)dict[current].faded == 0.0 || (double)dict[current].faded == 1.0)
                {
                    EditorGUILayout.BeginFadeGroup(value);
                }
            }

            public void Dispose()
            {
                if ((double)dict[current].faded == 0.0 || (double)dict[current].faded == 1.0)
                    return;
                EditorGUILayout.EndFadeGroup();
                dict.Remove(current);
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
            //centeredMiniLabel = new GUIStyle();
            //centeredMiniLabel.alignment = TextAnchor.MiddleCenter;
            //centeredMiniBoldLabel = new GUIStyle();
            //centeredMiniBoldLabel.alignment = TextAnchor.MiddleCenter;
            //rightAlignedMinilabel = new GUIStyle();
            //rightAlignedMinilabel.alignment = TextAnchor.MiddleRight;
            //tabToolBar = new GUIStyle("dragtab");
            //tabToolBar.onNormal.textColor = Color.white;
            //tabToolBar.fontSize = 9;
            //tabToolBar.alignment = TextAnchor.MiddleCenter;
            //
        }
    }
    // additional Tooltip 
    class Tooltip
    {
        public static void Generate(string text)
        {
            var propRect = GUILayoutUtility.GetLastRect();
            GUI.Label(propRect, new GUIContent("", text));
        }

        public static void Get()
        {

        }
    }
    // target loader interface
    public abstract class CustomLoader
    {
        public See1View _view;

        public CustomLoader(See1View view)
        {
            this._view = view;
        }

        public abstract void OnEnable();
        public abstract void OnClickButton();
        public abstract void OnGUI();
        public abstract void OnDisable();
    }

#endregion //----------------------------------------------------------------------------------------------------------------------------------------------------

    public class See1View : EditorWindow
    {

        #region Properties & Fields
        //settings shortcut (singleton)
        private DataManager dataManager
        {
            get { return DataManager.instance; }
        }
        //current data shortcut
        private See1ViewData currentData
        {
            get { return dataManager.current; }
        }

        //main objects
        PreviewRenderUtility _preview;
        GameObject _tempObj;
        GameObject _tempPickedObject;
        GameObject _mainTarget;
        public GameObject MainTarget => _mainTarget;
        Dictionary<GameObject, GameObject> _targetDic = new Dictionary<GameObject, GameObject>(); //멀티오브젝트 검사용

        //GameObject _shadowGo; //Hacky ShadowCaster
        ReflectionProbe _probe;

        Transform _lightPivot;
        Renderer _floor;
        CustomLoader _customLoader;


        //Animation
        List<AnimationPlayer> _playerList = new List<AnimationPlayer>();
        public UnityEvent onStopPlaying = new UnityEvent();

        public bool isPlaying
        {
            get { return _playerList.Count > 0 ? _playerList[0].isPlaying : false; }
        }

        //GUI & Control
        RectSlicer _rs;
        Rect _viewPortRect;
        Rect _controlRect;
        Vector2 _scrollPos;
        Vector2 _scrollPos1;
        bool _isStartDragValid = false;
        float _deltaTime;
        double _lastTimeSinceStartup = 0f;
        const int _labelWidth = 95;
        const int _toolbarHeight = 21; //oldskin 18 newskin 21
        TransformTreeView _treeView;
        TreeViewState _treeViewState;
        TargetInfo _targetInfo = new TargetInfo();
        bool _shortcutEnabled;
        SidePanelMode panelMode = SidePanelMode.View;
        PopupWindow _popup;
        SizePopup _sizePopup;
        bool _guiEnabled = true;
        bool _overlayEnabled = true;
        AnimBoolS splashEnabled = new AnimBoolS(false);
        AnimBoolS helpEnabled = new AnimBoolS(false);

        //Camera & Render
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

        ViewMode _viewMode = ViewMode.None;
        GizmoMode _gizmoMode = 0;
        int _previewLayer;
        const bool _updateFOV = false;

        private float _destFOV;

        Vector2 _destRot = new Vector2(180, 0);

        //Vector2 _destLightRot = new Vector2(180, 0);
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

        Recent<GameObject> _recentModel;
        Recent<AnimationClip> _recentAnimation;
        Recent<RenderPipelineAsset> _recentRPAsset;
#if UNITY_POST_PROCESSING_STACK_V2
        Recent<PostProcessProfile> _recentPostProcessProfile;
#endif
#if URP || HDRP
        Recent<VolumeProfile> _recentVolumeProfile;
#endif
        //Info
        GUIContent _viewInfo;
        readonly StringBuilder _sb0 = new StringBuilder();

        #endregion

        #region Unity Events & Callbacks

        void Awake()
        {
            InitPreviewLayerID();
        }

    void OnFocus()
        {
            _shortcutEnabled = true;
        }

        void OnLostFocus()
        {
            _shortcutEnabled = false;
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
            //기본 초기화
            Create();
            RegisterShortcut();
            EditorSceneManager.newSceneCreated += this.OnOpenNewScene;
            Updater.CheckForUpdates();
        }

        void OnDisable()
        {
            //기타 작업
            if (_popup) _popup.Close();
            dataManager.current.lastLighting = GetCurrentLighting();
            dataManager.current.lastView = new View(_destRot, _destDistance, _destPivotPos, _preview.cameraFieldOfView);
            DataManager.Save();
            _customLoader?.OnDisable();
            //기본 해제
            EditorSceneManager.newSceneCreated -= this.OnOpenNewScene;
            Shortcuts.Clear();
            Cleanup();
            //마무으리
            GC.Collect();
            Resources.UnloadUnusedAssets();
        }

        void Update()
        {
            SetEditorDeltaTime();
            if (_autoRotateCamera)
            {
                var rot = new Vector2(10, 0) * (_deltaTime * _cameraAutoRotationSpeed);
                UpdateCamera(rot, Vector2.zero, 0);
            }

            if (_autoRotateLight)
            {
                var rot = new Vector2(10, 0) * (_deltaTime * _lightAutoRotationSpeed);
                UpdateLight(rot);
            }

            for (int i = 0; i < _playerList.Count; i++)
            {
                _playerList[i].Update(_deltaTime);
            }

            SetMaterialProperties();
            FPS.Calculate(_deltaTime);
            // Draw Floor
            _floor.transform.position = Vector3.zero + new Vector3(0, currentData.floorHeight, 0);
            _floor.transform.localScale = Vector3.one * currentData.floorScale;
            Repaint();
        }

        void OnGUI()
        {
            if (_preview == null) return;
            if (!_preview.camera) return;
            GUI.enabled = _guiEnabled;
            using (EditorHelper.LabelWidth.Do(_labelWidth))
            {
                using (EditorHelper.PrefixLabelSize.Do(EditorStyles.miniLabel.font, 10))
                {
                    _viewPortRect = IsDocked() ? _rs.full : _rs.center;
                    _controlRect = new Rect(_rs.center.position.x, _rs.center.position.y + _rs.center.size.y - 120,
                        _rs.center.size.x, 120);

                    ProcessInput();
                    OnGUI_Viewport(_viewPortRect);
                    OnGUI_Top(_rs.top);
                    OnGUI_Bottom(_rs.bottom);
                    OnGUI_Left(_rs.stretchedLeft);
                    OnGUI_Right(_rs.stretchedRight);
                    OnGUI_AnimationControl(_controlRect);
                    OnGUI_ParticleSystemControl(_controlRect);
                    OnGUI_Info(_viewPortRect);
                    OnGUI_Log(_viewPortRect);
                    if (!_guiEnabled)
                        EditorGUI.DrawRect(_rs.full, Color.black * 0.5f);
                    if (_overlayEnabled)
                        EditorGUI.DrawRect(_controlRect, Color.black * 0.1f);

                    OnGUI_Gizmos(_viewPortRect);

                    //Splash
                    splashEnabled.target = !_mainTarget;

                    using (EditorHelper.Colorize.Do(Color.white * splashEnabled.faded, Color.white * splashEnabled.faded))
                    {
                        var logo = GUIContents.logoTexture;
                        Rect logoRect = new Rect(_viewPortRect.position + new Vector2(_viewPortRect.size.x * 0.5f, _viewPortRect.size.y * 0.5f) - new Vector2(80f, 64f), new Vector2(160f, 128f));
                        Rect titleRect = new Rect(logoRect.position + new Vector2(0, logoRect.size.y), GUILayoutUtility.GetRect(GUIContents.title, Styles.centeredBigLabel, GUILayout.Width(160)).size);
                        Rect copyrightRect = new Rect(titleRect.position + new Vector2(0, titleRect.size.y), GUILayoutUtility.GetRect(GUIContents.copyright, Styles.centeredMiniLabel, GUILayout.Width(160)).size);
                        Rect btnRect = new Rect(copyrightRect.position + new Vector2(55f, copyrightRect.size.y + 10f), new Vector2(50f, 20f));
                        var logoFaded = (1 - helpEnabled.faded) * splashEnabled.faded;

                        using (EditorHelper.Colorize.Do(Color.white * logoFaded, Color.white * logoFaded))
                        {
                            using (new EditorGUI.DisabledScope(helpEnabled.target))
                            {
                                //var logo = EditorGUIUtility.IconContent("d_SceneAsset Icon").image;
                                GUI.DrawTexture(logoRect, logo, ScaleMode.ScaleToFit, true, 1, new Color(0.85f, 0.85f, 0.85f) * logoFaded, 0, 0);
                                EditorGUI.DropShadowLabel(titleRect, GUIContents.startup, Styles.centeredBigLabel);
                                EditorGUI.DropShadowLabel(copyrightRect, GUIContents.copyright, Styles.centeredMiniLabel);
                                if (GUI.Button(btnRect, "Help", EditorStyles.miniButton))
                                {
                                    helpEnabled.target = true;
                                }
                            }

                        }
                        var helpFaded = helpEnabled.faded * splashEnabled.faded;

                        using (EditorHelper.Colorize.Do(Color.white * helpFaded, Color.white * helpFaded))
                        {
                            using (new EditorGUI.DisabledScope(!helpEnabled.target))
                            {
                                Rect helpRect = new Rect(_viewPortRect.position + new Vector2(_viewPortRect.size.x * 0.5f, _viewPortRect.size.y * 0.5f) - new Vector2(200f, 200f), new Vector2(400f, 400f));

                                EditorGUILayout.LabelField("Help");
                                if (GUI.Button(btnRect, "Back", EditorStyles.miniButton))
                                {
                                    helpEnabled.target = false;
                                }
                                EditorGUI.DrawRect(helpRect, Color.black * 0.5f);
                                EditorGUI.DropShadowLabel(helpRect, GUIContents.help, Styles.centeredMiniLabel);
                            }
                            //EditorGUI.DrawRect(logoRect, Color.red * 0.5f);
                            //EditorGUI.DrawRect(titleRect, Color.green * 0.5f);
                            //EditorGUI.DrawRect(copyrightRect, Color.blue * 0.5f);
                            //EditorGUI.DrawRect(helpRect, Color.black * 0.5f);
                        }
                    }
                }
            }
        }
        void OnSelectionChange()
        {
            if (!(currentData.modelCreateMode == ModelCreateMode.Preview)) return;
            if (Validate(Selection.activeGameObject) == false) return;
            _tempObj = Selection.activeGameObject;
            AddModel(_tempObj, true);
        }

        void OnOpenNewScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            Create();
        }

        #endregion

        #region MainMehods

        void RegisterShortcut()
        {
            Shortcuts.Clear();

            Shortcuts.AddBlank(new GUIContent("L Mouse Drag - Rotate Camera"));
            Shortcuts.AddBlank(new GUIContent("R Mouse Drag - Rotate Light"));
            Shortcuts.AddBlank(new GUIContent("L Mouse Double Click - Reframe"));
            Shortcuts.AddBlank(new GUIContent("R Mouse Double Click - Reset Light"));

            Shortcuts.AddBlank(new GUIContent("-------------------------------------"));

            Shortcuts.Add(KeyCode.Alpha0, new GUIContent("ApplyView 0"), () => ApplyView(0));
            Shortcuts.Add(KeyCode.Alpha1, new GUIContent("ApplyView 1"), () => ApplyView(1));
            Shortcuts.Add(KeyCode.Alpha2, new GUIContent("ApplyView 2"), () => ApplyView(2));
            Shortcuts.Add(KeyCode.Alpha3, new GUIContent("ApplyView 3"), () => ApplyView(3));
            Shortcuts.Add(KeyCode.Alpha4, new GUIContent("ApplyView 4"), () => ApplyView(4));
            Shortcuts.Add(KeyCode.Alpha5, new GUIContent("ApplyView 5"), () => ApplyView(5));
            Shortcuts.Add(KeyCode.Alpha6, new GUIContent("ApplyView 6"), () => ApplyView(6));
            Shortcuts.Add(KeyCode.Alpha7, new GUIContent("ApplyView 7"), () => ApplyView(7));
            Shortcuts.Add(KeyCode.Alpha8, new GUIContent("ApplyView 8"), () => ApplyView(8));
            Shortcuts.Add(KeyCode.Alpha9, new GUIContent("ApplyView 9"), () => ApplyView(9));

            Shortcuts.AddBlank(new GUIContent("-------------------------------------"));

            Shortcuts.Add(KeyCode.F, new GUIContent("Front View"), () => _destRot = new Vector2(180, 0));
            Shortcuts.Add(KeyCode.K, new GUIContent("Back View"), () => _destRot = Vector2.zero);
            Shortcuts.Add(KeyCode.L, new GUIContent("Left View"), () => _destRot = new Vector2(90, 0));
            Shortcuts.Add(KeyCode.R, new GUIContent("Right View"), () => _destRot = new Vector2(-90, 0));
            Shortcuts.Add(KeyCode.T, new GUIContent("Top View"), () => _destRot = new Vector2(180, 90));
            Shortcuts.Add(KeyCode.B, new GUIContent("Bottom View"), () => _destRot = new Vector2(180, -90));
            Shortcuts.Add(KeyCode.W, new GUIContent("Move Toward"), () => _destDistance -= 0.01f);
            Shortcuts.Add(KeyCode.S, new GUIContent("Move Backward"), () => _destDistance += 0.01f);
            Shortcuts.Add(KeyCode.A, new GUIContent("Move Left"),
                () => _destPivotPos += _preview.camera.transform.rotation * new Vector3(-0.01f, 0));
            Shortcuts.Add(KeyCode.D, new GUIContent("Move Right"),
                () => _destPivotPos += _preview.camera.transform.rotation * new Vector3(0.01f, 0));

            Shortcuts.AddBlank(new GUIContent("-------------------------------------"));

            Shortcuts.Add(KeyCode.G, new GUIContent("Toggle Grid"), () =>
            {
                _gridEnabled = !_gridEnabled;
                ApplyModelCommandBuffers();
            });
            Shortcuts.Add(KeyCode.P, new GUIContent("Toggle Perspective"),
                () => _preview.camera.orthographic = !_preview.camera.orthographic);
            Shortcuts.Add(KeyCode.F1, new GUIContent("Render"), () => RenderAndSaveFile());
            Shortcuts.Add(KeyCode.F2, new GUIContent("Toggle Color"), () =>
            {
                _colorEnabled = !_colorEnabled;
                ApplyModelCommandBuffers();
            });
            Shortcuts.Add(KeyCode.F3, new GUIContent("Toggle Wireframe"), () =>
            {
                _wireFrameEnabled = !_wireFrameEnabled;
                ApplyModelCommandBuffers();
            });
            Shortcuts.Add(KeyCode.F4, new GUIContent("Toggle Shadow"), () =>
            {
                dataManager.current.planeShadowEnabled = !dataManager.current.planeShadowEnabled;
                ApplyModelCommandBuffers();
            });
            Shortcuts.Add(KeyCode.Escape, new GUIContent("Toggle Gizmo"), () => _gizmoMode = ~_gizmoMode);
            Shortcuts.Add(KeyCode.Delete, new GUIContent("Delete Target"), () =>
            {
                var selected = Selection.activeGameObject;
                if (_targetDic.ContainsValue(selected))
                {
                    RemoveModel(selected);
                }
            });
            Shortcuts.Add(KeyCode.Space, new GUIContent("Toggle Play"),
                () => _playerList.FirstOrDefault().TogglePlay());
            Shortcuts.Add(KeyCode.BackQuote, new GUIContent("Toggle Overlay"),
                () => _overlayEnabled = !_overlayEnabled);
        }

        //        void SetModel(GameObject prefab)
        //        {
        //            if (!prefab) return;
        //            if (prefab.GetType() != typeof(GameObject)) return;
        //            if (_mainTarget) DestroyImmediate(_mainTarget);
        //            //if (_shadowGo) DestroyImmediate(_shadowGo);
        //            _mainTarget = Instantiate(prefab) as GameObject;

        //            //_shadowGo = Instantiate(prefab);
        //            if (_mainTarget != null)
        //            {
        //#if UNITY_2017
        //                PrefabUtility.DisconnectPrefabInstance(_mainTarget); //새로 바뀐 프리팹 관리 때문에 optimize 적용이 힘듬.
        //#endif
        //                _mainTarget.name = prefab.name;
        //                //_shadowGo.name = prefab.name + "_Shadow";

        //                SetFlagsAll(_mainTarget, HideFlags.HideAndDontSave);
        //                //SetFlagsAll(_shadowGo, HideFlags.HideAndDontSave);
        //                SetLayerAll(_mainTarget, _previewLayer);
        //                //SetLayerAll(_shadowGo, _previewLayer);
        //                //ShowHideAll(_shadowGo, false);
        //                _preview.AddSingleGO(_mainTarget);
        //                _targetInfo.Init(_mainTarget);

        //                //etc
        //                if (currentData.reframeToTarget) FitTargetToViewport();
        //                currentData.lastTarget = prefab;
        //                Selection.activeGameObject = _mainTarget;
        //                if (_treeView != null)
        //                {
        //                    _treeView.Reload();
        //                }

        //                Notice.Log(string.IsNullOrEmpty(_targetInfo.assetPath) ? prefab.name : _targetInfo.assetPath, false);
        //                SetAnimation(_mainTarget, true);
        //                ApplyModelCommandBuffers();
        //                Repaint();
        //                //_fade.target = true;
        //                //_fade.target = false;
        //            }
        //        }
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
            if(isPrefab)
            {
                // 소스가 프리팹이면 여기에서 적절하게 처리.
                instance = PrefabUtility.InstantiatePrefab(src) as GameObject;
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction); 
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
                _preview.AddSingleGO(instance);
                _targetInfo.Init(src,instance);
                _treeView?.Reload();
                InitAnimationPlayer(_mainTarget, true);
                ApplyModelCommandBuffers();
                if (currentData.forceUpdateComponent)
                {
                    foreach (var b in _targetInfo.behaviours)
                    {
                        RunInEditHelper2.Add(b);
                    }
                    RunInEditHelper2.Start();
                }
                // 마무리
                Repaint();
                if (dataManager.current.reframeToTarget) FitTargetToViewport();
                _recentModel.Add(_targetInfo.assetPath);
                Notice.Log(string.IsNullOrEmpty(_targetInfo.assetPath) ? src.name : _targetInfo.assetPath, false);
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
            if (_treeView != null)
            {
                _treeView.Reload();
            }
            RunInEditHelper2.Clean();
            Notice.Log(string.Format("{0} Removed", name), false);
            ResetAnimationPlayer();
            ApplyModelCommandBuffers();
            Repaint();
        }
        //Shortcut for add animation quickly
        private void AddAnimationAndPlay(AnimationClip clip)
        {

            AddAnimation(clip, true);
        }
        private void AddAnimation(AnimationClip clip, bool instantPlay = false)
        {
            if (_playerList.Count > 0)
            {
                _playerList[0].AddClip(clip);
                _recentAnimation.Add(AssetDatabase.GetAssetPath(clip));
                if (instantPlay) _playerList[0].PlayInstant(clip);
            }
        }

        void Create()
        {
            Cleanup();
            _rs = new RectSlicer(this);
            _rs.topTargetHeight = _toolbarHeight; //Styles.GetToolbarHeight();
            _rs.bottomTargetHeight = _toolbarHeight; //Styles.GetToolbarHeight();
            _rs.leftTargetWidth = 200;
            _rs.rightTargetWidth = 250;
            _rs.openTop.target = true;
            _rs.openBottom.target = false;
            _rs.openLeft.target = false;
            _rs.openRight.target = true;

            _sizePopup = new SizePopup();
            _preview = new PreviewRenderUtility(true, true);
            _camTr = _preview.camera.transform;

            _preview.camera.cullingMask = ~_previewLayer;
            _preview.camera.fieldOfView = 30.0f;
            _preview.camera.nearClipPlane = 0.5f;
            _preview.camera.farClipPlane = 30;
            _preview.camera.clearFlags = CameraClearFlags.Color;
            _preview.camera.backgroundColor = Color.black;
            _preview.camera.allowMSAA = true;
            _preview.camera.allowDynamicResolution = true;
            _preview.camera.allowHDR = true;
            _preview.camera.cameraType = CameraType.Preview;
            _preview.ambientColor = Color.gray;
            _preview.camera.gameObject.layer = _previewLayer;

            //_defaultMaterial = DefaultMaterial.Get(currentData.renderPipelineMode);
            _skyMaterial = new Material(FindShader("Skybox/Cubemap"));

            _colorCommandBuffer = new CommandBuffer();
            _colorCommandBuffer.name = string.Format("{0} {1}", this.name, "Color");
            _colorMaterial = new Material(FindBuiltinShader("Internal-Colored.shader"));

            _gridCommandBuffer = new CommandBuffer();
            _gridCommandBuffer.name = string.Format("{0} {1}", this.name, "Grid");
            _gridMaterial = new Material(FindShader("Sprites/Default"));

            _wireCommandBuffer = new CommandBuffer();
            _wireCommandBuffer.name = string.Format("{0} {1}", this.name, "WireFrame");
            //_wireMaterial = new Material(FindShader("See1View/Wireframe"));
            _wireMaterial = new Material(Shaders.wireFrame);

            _shadowCommandBuffer = new CommandBuffer();
            _shadowCommandBuffer.name = string.Format("{0} {1}", this.name, "Shadow");
            //_shadowMaterial = new Material(FindShader("See1View/PlanarShadow")); //PreviewCamera RT has no stencil buffer. OTL
            _shadowMaterial = new Material(Shaders.planarShadow);

            _heightFogCommandBuffer = new CommandBuffer();
            _heightFogCommandBuffer.name = string.Format("{0} {1}", this.name, "HeightFog");
            //_heightFogMaterial = new Material(FindShader("See1View/HeightFog")); //PreviewCamera RT has no stencil buffer. OTL
            _heightFogMaterial = new Material(Shaders.heightFog);

            _depthCommandBuffer = new CommandBuffer();
            _depthCommandBuffer.name = string.Format("{0} {1}", this.name, "Depth");
            _depthMaterial = new Material(Shaders.depth);
            //_depthMaterial = new Material(FindShader("See1View/Depth"));

            _depthNormalCommandBuffer = new CommandBuffer();
            _depthNormalCommandBuffer.name = string.Format("{0} {1}", this.name, "DepthNormal");
            _depthNormalMaterial = new Material(Shaders.depthNormal);
            //_depthNormalMaterial = new Material(FindShader("See1View/DepthNormal"));

            var camPivotGo = EditorUtility.CreateGameObjectWithHideFlags("CamPivot", HideFlags.HideAndDontSave);
            _camPivot = camPivotGo.transform;
            _preview.AddSingleGO(camPivotGo);

            var lightPivotGo = EditorUtility.CreateGameObjectWithHideFlags("LightPivot", HideFlags.HideAndDontSave);
            _lightPivot = lightPivotGo.transform;
            _probe = lightPivotGo.AddComponent<ReflectionProbe>();
            _probe.mode = ReflectionProbeMode.Custom;
            _probe.size = Vector3.one * 100;
            _probe.cullingMask = ~_previewLayer;

            _preview.AddSingleGO(lightPivotGo);

            var floorGo = EditorUtility.CreateGameObjectWithHideFlags("Floor", HideFlags.HideAndDontSave);
            var floorFilter = floorGo.AddComponent<MeshFilter>();
            floorFilter.sharedMesh = Quad.Get();
            _floor = floorGo.AddComponent<MeshRenderer>();
            _floor.sharedMaterial = DefaultMaterial.Get(currentData.renderPipelineMode);
            SetLayerAll(floorGo, _previewLayer);
            _preview.AddSingleGO(floorGo);

            InitTreeView();
            ResetLight();
            //Apply Settings From Data
            InitializePipeline();
            InitializePostProcess();
            onChangeRenderPipeline.AddListener(InitializePipeline);
            onChangeRenderPipeline.AddListener(InitializePostProcess);
            DataManager.onDataChanged.AddListener(InitializePipeline);
            DataManager.onDataChanged.AddListener(InitializePostProcess);

            // Custom Loader
            _customLoader = Initializer.CreateCustomLoader(this);
            _customLoader?.OnEnable();

            _tempObj = currentData.lastTarget;
            AddModel(_tempObj, true);
            ApplyView(dataManager.current.lastView);
            ApplyBackground();
            ApplyReflectionEnvironment();
            ApplyLighting(dataManager.current.lastLighting);
            _recentModel = new Recent<GameObject>(10);
            _recentModel.onClickEvent += AddModel;
            _recentAnimation = new Recent<AnimationClip>(10);
            _recentAnimation.onClickEvent += AddAnimationAndPlay;
#if UNITY_POST_PROCESSING_STACK_V2
            _recentPostProcessProfile = new Recent<PostProcessProfile>(10);
            _recentPostProcessProfile.onClickEvent += SetPostProcessProfile;
#endif
#if URP || HDRP
            _recentVolumeProfile = new Recent<VolumeProfile>(10);
            _recentVolumeProfile.onClickEvent += SetVolumeProfile;
#endif
        }

        private void InitializePipeline()
        {
            if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
            {

            }
#if URP
            //if (_urpCamera) DestroyImmediate(_urpCamera); //여러 파이프라인이 사용 가능한 상황을 위해 기본적으로 제거하고 시작.
            if (currentData.renderPipelineMode == RenderPipelineMode.Universal)
            {
                _urpCamera = _preview.camera.GetUniversalAdditionalCameraData();
                _urpCamera.volumeLayerMask = ~_previewLayer;
                _preview.camera.cameraType = currentData.cameraType;
                //_urpRenderer = (UniversalRendererData)ScriptableObject.CreateInstance<UniversalRendererData>();                
            }
#endif
#if HDRP
            if (currentData.renderPipelineMode == RenderPipelineMode.HighDefinition)
            {
                bool hdrpCamExists = _preview.camera.gameObject.TryGetComponent<HDAdditionalCameraData>(out _hdrpCamera);
                if (!hdrpCamExists)
                {
                    _hdrpCamera = _preview.camera.gameObject.AddComponent<HDAdditionalCameraData>();
                }
                _hdrpCamera.volumeLayerMask = ~_previewLayer;
                _preview.camera.cameraType = currentData.cameraType;
            }
#endif
            Notice.Log(string.Format("{0} Render Pipeline Initialized", currentData.renderPipelineMode.ToString()));
        }

        void Cleanup()
        {
            if (_camPivot) DestroyImmediate(_camPivot.gameObject);
            if (_lightPivot) DestroyImmediate(_lightPivot.gameObject);
            if (_floor) DestroyImmediate(_floor.gameObject);

            if (_skyMaterial) DestroyImmediate(_skyMaterial);
            if (_preview != null)
            {
                RenderTexture.active = null;
                _preview.Cleanup(); //Handle.SetCamera 에서 RenderTexure.active 관련 warning 발생
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

        void InitTreeView()
        {
            var fi = _preview.GetType().GetField("m_PreviewScene", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null)
            {
                var previewScene = fi.GetValue(_preview);
                var scene = (UnityEngine.SceneManagement.Scene)(previewScene.GetType()
                    .GetField("m_Scene", BindingFlags.Instance | BindingFlags.NonPublic)).GetValue(previewScene);
                if (_treeViewState == null)
                    _treeViewState = new TreeViewState();
                _treeView = new TransformTreeView(scene, _treeViewState);
                _treeView.onDragObject = (go) => { AddModel(go, false); };
            }
        }

        void SetMaterialProperties()
        {
            if (_skyMaterial)
            {
                _skyMaterial.SetTexture("_Tex", currentData.cubeMap);
                //_skyMaterial.SetFloat("_Rotation", _preview.lights[0].transform.rotation.eulerAngles.y);
            }
            if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
            {
                if (_colorMaterial && _colorEnabled)
                {
                    _colorMaterial.SetColor("_Color", _color);
                }

                if (_gridMaterial && _gridEnabled)
                {
                    _gridMaterial.SetColor("_Color", _gridColor);
                }

                if (_wireMaterial && _wireFrameEnabled)
                {
                    _wireMaterial.SetColor("_LineColor", currentData.wireLineColor);
                    _wireMaterial.SetColor("_FillColor", currentData.wireFillColor);
                    _wireMaterial.SetFloat("_WireThickness", currentData.wireThickness);
                    _wireMaterial.SetFloat("UseDiscard", currentData.wireUseDiscard);
                }

                if (_shadowMaterial && currentData.planeShadowEnabled)
                {
                    _shadowMaterial.SetColor("_ShadowColor", currentData.planeShadowColor);
                    _shadowMaterial.SetFloat("_PlaneHeight", _targetInfo.bounds.min.y);
                }

                if (_heightFogMaterial && currentData.heightFogEnabled)
                {
                    _heightFogMaterial.SetColor("_Color", currentData.heightFogColor);
                    _heightFogMaterial.SetFloat("_Ground", _targetInfo.bounds.min.y);
                    _heightFogMaterial.SetFloat("_Height", currentData.heightFogHeight);
                }

                if (_depthMaterial && _viewMode == ViewMode.Depth)
                {
                    _depthMaterial.SetFloat("_Seperate", _screenSeparate);
                }

                if (_depthNormalMaterial && _viewMode == ViewMode.Normal)
                {
                    _depthNormalMaterial.SetFloat("_Seperate", _screenSeparate);
                }
            }
        }

        private Lighting GetCurrentLighting()
        {
            var lighting = new Lighting();
            lighting.ambientSkyColor = dataManager.current.ambientSkyColor;
            foreach (var light in _preview.lights)
            {
                var info = new Lighting.LightInfo();
                info.position = light.transform.position;
                info.rotation = light.transform.rotation;
                info.lightColor = light.color;
                info.intensity = light.intensity;
                lighting.lightList.Add(info);
            }

            return lighting;
        }

        static bool Validate(Object obj)
        {

            //is Null?
            if (!obj) return false;
            //is GameObject?
            GameObject go = obj as GameObject;
            if (!go) return false;
            //has Renderer?
            if (go.GetComponentsInChildren<Renderer>().Length < 1) return false;
            //is Project Asset?
            if (go.scene.isLoaded) return false;
            //ok let's load
            return true;
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

        void UnlockInspector()
        {
            _preview.camera.gameObject.hideFlags = HideFlags.None;
            _preview.lights[0].gameObject.hideFlags = HideFlags.None;
            _preview.lights[1].gameObject.hideFlags = HideFlags.None;
            _camPivot.gameObject.hideFlags = HideFlags.None;
            _lightPivot.gameObject.hideFlags = HideFlags.None;
            if (_mainTarget) SetFlagsAll(_mainTarget.gameObject, HideFlags.None);
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        //private void DataChanged()
        //{
        //    _list = AS_PartList.Create(settings.currentData, PartChanged, TargetItemHandler, MenuItemHandler);
        //    Selection.activeObject = settings.currentData;
        //    SetModel(true);
        //    EditorUtility.SetDirty(settings);
        //}

        //private void PartChanged()
        //{
        //    SetModel(false);
        //    EditorUtility.SetDirty(settings.currentData);
        //}

        //private void TargetItemHandler(object target)
        //{
        //    PartData pData = target as PartData;
        //    ShowMenu(pData.m_TargetPath, _modelRootHierachy, _modelRootHierachy, (x) =>
        //    {
        //        pData.m_TargetPath = (string)x;
        //        SetModel(false);
        //        EditorUtility.SetDirty(settings.currentData);
        //    });
        //}

        //private void MenuItemHandler(object target)
        //{
        //    PartData pData = target as PartData;
        //    if (pData != null)
        //    {
        //        settings.currentData.PartDataList.Add(pData);
        //        EditorUtility.SetDirty(settings.currentData);
        //    }
        //}
        #endregion

        #region CommandBuffer and Render

        void SetGridBuffer(bool set)
        {
            CommandBufferManager.RemoveBufferFromAllEvent(_preview.camera, _gridCommandBuffer);
            _gridCommandBuffer.Clear();
            if (set)
            {
                _preview.camera.AddCommandBuffer(CameraEvent.BeforeImageEffects, _gridCommandBuffer);
                Vector3 translation = new Vector3(-_gridSize / 2, 0, -_gridSize / 2); //translate to grid center
                Matrix4x4 m = Matrix4x4.Translate(translation);
                _gridCommandBuffer.DrawMesh(Grid.Get(_gridSize), m, _gridMaterial);
            }
        }

        void SetModelRenderBuffer(CameraEvent cameraEvent, CommandBuffer buffer, Material mat, bool set)
        {
            CommandBufferManager.RemoveBufferFromAllEvent(_preview.camera, buffer);
            //_preview.camera.RemoveCommandBuffer(cameraEvent, buffer);
            buffer.Clear();
            if (_mainTarget && set)
            {
                _preview.camera.AddCommandBuffer(cameraEvent, buffer);
                var renderers = _mainTarget.GetComponentsInChildren<Renderer>();
                for (var i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];

                    var smr = renderer as SkinnedMeshRenderer;
                    var mr = renderer as MeshRenderer;
                    if (smr)
                    {
                        for (int j = 0; j < smr.sharedMesh.subMeshCount; j++)
                        {
                            int submeshIndex = j;
                            buffer.DrawRenderer(renderer, mat, submeshIndex, -1); //-1 renders all passes
                        }
                    }
                    else if (mr)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        for (int j = 0; j < mf.sharedMesh.subMeshCount; j++)
                        {
                            int submeshIndex = j;
                            buffer.DrawRenderer(renderer, mat, submeshIndex, -1);
                        }
                    }
                }
            }
        }

        void SetCameraTargetBlitBuffer(CameraEvent cameraEvent, CommandBuffer buffer, Material mat, bool set)
        {
            CommandBufferManager.RemoveBufferFromAllEvent(_preview.camera, buffer);
            buffer.Clear();
            if (_mainTarget && set)
            {
                int nameID = Shader.PropertyToID("See1View");
                _preview.camera.AddCommandBuffer(cameraEvent, buffer);
                //camera.targetTexture 를 사용할 시 창 사이즈가 변경되면 렌더텍스쳐가 새로 할당되며 레퍼가 날아가므로 BuiltinRenderTextureType 이나 ID 를 통해 접근해야함. 
                //Todo 포맷이 Default 라서 알파가 없음.
                buffer.GetTemporaryRT(nameID, _preview.camera.targetTexture.width, _preview.camera.targetTexture.height, 32, FilterMode.Trilinear);
                //src 와 dst 가 같으면 안되니까 하나 만들어야함 얃얃
                buffer.Blit(BuiltinRenderTextureType.CameraTarget, nameID, mat);
                buffer.Blit(nameID, BuiltinRenderTextureType.CameraTarget);
                buffer.ReleaseTemporaryRT(nameID);
            }
        }

        void ApplyModelCommandBuffers()
        {
            SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _colorCommandBuffer, _colorMaterial, _colorEnabled);
            SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _wireCommandBuffer, _wireMaterial, _wireFrameEnabled);
            SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _heightFogCommandBuffer, _heightFogMaterial, currentData.heightFogEnabled);
            SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _shadowCommandBuffer, _shadowMaterial, currentData.planeShadowEnabled);
        }

        void ApplyCameraCommandBuffers()
        {
            CommandBufferManager.RemoveBufferFromAllEvent(_preview.camera, _depthCommandBuffer);
            CommandBufferManager.RemoveBufferFromAllEvent(_preview.camera, _depthNormalCommandBuffer);
            switch (_viewMode)
            {
                case ViewMode.None:
                    _preview.camera.depthTextureMode = DepthTextureMode.None;
                    break;
                case ViewMode.Depth:
                    _preview.camera.depthTextureMode = DepthTextureMode.Depth;
                    SetCameraTargetBlitBuffer(CameraEvent.BeforeImageEffects, _depthCommandBuffer, _depthMaterial, true);
                    break;
                case ViewMode.Normal:
                    _preview.camera.depthTextureMode = DepthTextureMode.DepthNormals;
                    SetCameraTargetBlitBuffer(CameraEvent.BeforeImageEffects, _depthNormalCommandBuffer, _depthNormalMaterial, true);
                    break;
            }
        }
#if UNITY_POST_PROCESSING_STACK_V2
        void SetPostProcessProfile(PostProcessProfile profile)
        {
            currentData.profile = profile;
            InitializePostProcess();
            _recentPostProcessProfile.Add(AssetDatabase.GetAssetPath(profile));
        }
#endif
#if URP || HDRP
        void SetVolumeProfile(VolumeProfile profile)
        {
            currentData.volumeProfile = profile;
            InitializePostProcess();
            _recentVolumeProfile.Add(AssetDatabase.GetAssetPath(profile));
        }
#endif
        void InitializePostProcess()
        {
            //Cleanup Firt.
#if UNITY_POST_PROCESSING_STACK_V2
            var postLayer = _preview.camera.gameObject.GetComponent<PostProcessLayer>();
            if (postLayer) DestroyImmediate(postLayer);
            var postVolume = _preview.camera.gameObject.GetComponent<PostProcessVolume>();
            if (postVolume) DestroyImmediate(postVolume);
#endif
#if URP || HDRP
            var volume = _preview.camera.gameObject.GetComponent<Volume>();
            if (volume) DestroyImmediate(volume);
#endif
            //Create by Context.
            if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
            {
#if UNITY_POST_PROCESSING_STACK_V2
                postLayer = _preview.camera.gameObject.GetComponent<PostProcessLayer>();
                postVolume = _preview.camera.gameObject.GetComponent<PostProcessVolume>();

                if (currentData.postProcessEnabled && currentData.profile)
                {
                    if (!postLayer) postLayer = _preview.camera.gameObject.AddComponent<PostProcessLayer>();
                    postLayer.antialiasingMode = true
                        ? PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing
                        : PostProcessLayer.Antialiasing.None;
                    if (!postVolume) postVolume = _preview.camera.gameObject.AddComponent<PostProcessVolume>();
                    postLayer.volumeLayer = -1;
                    postLayer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                    postLayer.fastApproximateAntialiasing.fastMode = true;
                    postLayer.fastApproximateAntialiasing.keepAlpha = true;
                    postVolume.isGlobal = true;
                    postVolume.profile = currentData.profile;
                }
                Notice.Log("Post Process Initialized");
#endif
            }
#if URP || HDRP
            else
            {
                volume = _preview.camera.gameObject.GetComponent<Volume>();
                if (currentData.postProcessEnabled && currentData.volumeProfile)
                {
                    if (!volume) volume = _preview.camera.gameObject.AddComponent<Volume>();
                    volume.isGlobal = true;
                    volume.profile = currentData.volumeProfile;
                }
#if URP
                _urpCamera.renderPostProcessing = currentData.postProcessEnabled;
#endif
                Notice.Log("Volume Initialized");
            }
#endif
        }

        Texture2D RenderToTexture(int multiplyer = 1, bool alpha = false)
        {
            int w = (int)_viewPortRect.size.x * multiplyer;
            int h = (int)_viewPortRect.size.y * multiplyer;
            _preview.BeginPreview(new Rect(_viewPortRect.position, new Vector2(w, h)), GUIStyle.none);
            bool enableSRP = currentData.renderPipelineMode != RenderPipelineMode.BuiltIn;
            using (new RenderPipelineOverrider(currentData.renderPipelineAsset))
            {
                using (new QualitySettingsOverrider())
                {
                    using (new ShowHideRendererScope(_floor,!currentData.alphaAppliedImage))
                    {
                        using (new RenderSettingsOverrider(AmbientMode.Flat, currentData.ambientSkyColor, _skyMaterial))
                        {
                            if (alpha)
                            {
                                CameraClearFlags clearFlags = _preview.camera.clearFlags;
                                Color backgroundColor = _preview.camera.backgroundColor;
                                _preview.camera.clearFlags = CameraClearFlags.Color;
                                _preview.camera.backgroundColor = Color.clear;
                                _preview.Render(enableSRP, _updateFOV);
                                _preview.camera.clearFlags = clearFlags;
                                _preview.camera.backgroundColor = backgroundColor;
                            }
                            else
                            {
                                _preview.Render(enableSRP, _updateFOV);
                            }
                        }
                    }
                }
            }

            Texture tex = _preview.EndPreview();
            RenderTexture temp = RenderTexture.GetTemporary(w ,                h , 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(tex, temp);
            GL.sRGBWrite = false;
            RenderTexture.active = temp;
            Texture2D tex2D = new Texture2D(temp.width, temp.height, alpha ? TextureFormat.ARGB32 : TextureFormat.RGB24,
                false, true);
            tex2D.ReadPixels(new Rect(0, 0, temp.width, temp.height), 0, 0);
            tex2D.Apply();
            RenderTexture.ReleaseTemporary(temp);
            return tex2D;
        }

        //void RenderAndSaveFile()
        //{
        //    Texture2D tex = RenderToTexture((int)currentData.captureMultiplier, currentData.screenshotAlpha);
        //    string savedPath = SaveAsFile(tex,
        //        Directory.GetParent(Application.dataPath).ToString() + "/Screenshots", _targetGo ? _targetGo.name : "Blank",
        //        "MMddHHmmss");
        //    if (currentData.openSavedImage)
        //    {
        //        EditorUtility.OpenWithDefaultApp(savedPath);
        //    }
        //}

        void RenderAndSaveFile()
        {
            Texture2D tex = RenderToTexture((int)currentData.imageSizeMultiplier, currentData.alphaAppliedImage);
            string baseName = _mainTarget ? _mainTarget.name : "Blank";
            string savedPath = SaveAsFile(tex, Directory.GetParent(Application.dataPath).ToString() + "/Screenshots",
                baseName, dataManager.current.imageSaveMode);
            if (currentData.openSavedImage)
            {
                EditorUtility.OpenWithDefaultApp(savedPath);
            }
        }

        private void AddViewportSize(Vector2 v2)
        {
            currentData.viewportSizes.Add(v2);
            currentData.viewportSizes = currentData.viewportSizes.Distinct().ToList();
            currentData.viewportSizes.Sort((emp1, emp2) => emp1.magnitude.CompareTo(emp2.magnitude));
        }

        private void ShowPopupWindow()
        {
            //float width = 350;
            //float height = 200;
            //var rect = new Rect(position.x + (position.width - width) / 2, position.y + (position.height - height) / 2, width, height);
            //PopupWindow.Show(rect, _sizePopup);
            _popup = ScriptableObject.CreateInstance<PopupWindow>() as PopupWindow;
            _popup.Init(this, v2 => { AddViewportSize(v2); }, () => { _guiEnabled = true; });
            _popup.ShowPopup();
            _guiEnabled = false;
        }

        #endregion

        #region Animation

        public void InitAnimationPlayer(GameObject root, bool reset)
        {
            if (!root)
            {
                _playerList.Clear();
                return;
            }
            //기존 수집된 애니메이터에 문제가 있는지 검사 (모델 옵션이 바뀜에 따라 있던 애니메이터가 없어지는 경우가 생김.
            if (_playerList.Any(x => x.actorList.Any(ani => ani.instance == null)))
            {
                reset = true;
            }

            //애니메이션이 재생중인 경우 모델이 바뀌어도 애니메이션은 초기화하지 않고 계속 재생하기 위해 여기에서 바로 Deoptimize 해줌.
            //Todo Animator 를 재수집해서 기존과 동일한 건 유지하고 아닌 건 새로 추가해줘야...
            if (!reset && _playerList.Count > 0)
            {
                foreach (var player in _playerList)
                {
                    if (player.isPlaying) player.DeOptimizeObject();
                }
            }
            //애니메이션 초기화 및 재수집. 일반적인 경우.
            else
            {
                _playerList.Clear();
                Animator[] animators = root.GetComponentsInChildren<Animator>();
                if (animators.Length > 0)
                {
                    for (int i = 0; i < animators.Length; i++)
                    {
                        Animator animator = animators[i];
                        AnimationPlayer player = new AnimationPlayer();
                        player.AddActor(animator.gameObject, true);
                        player.onStopPlaying = onStopPlaying;
                        _playerList.Add(player);
                    }
                }
                else
                {
                    //Create Default Animator Component
                    AnimationPlayer player = new AnimationPlayer();
                    player.AddActor(root, true);
                    player.onStopPlaying = onStopPlaying;
                    _playerList.Add(player);
                }
            }
        }

        public void ResetAnimationPlayer()
        {
            _playerList.Clear();
        }

        public void AddModifier(TransformModifier modifier)
        {
            _playerList[0].AddModifier(modifier);
        }

        #endregion

        #region GUIContents

        public class GUIContents
        {
            internal static GUIContent title = new GUIContent(Initializer.title, EditorGUIUtility.IconContent("ViewToolOrbit").image, Initializer.title);
            internal static GUIContent startup = new GUIContent(Initializer.title);
            internal static GUIContent copyright = new GUIContent(Initializer.description);
            internal static GUIContent help = new GUIContent(Initializer.help);
            public static GUIContent enableSRP = new GUIContent("Enable SRP", "스크립터블 렌더 파이프라인을 활성화합니다.");
            public static GUIContent currentPipeline = new GUIContent("Pipeline Asset", "사용할 렌더 파이프라인 애셋을 선택합니다.\n비워놓으면 Builtin 파이프라인이 사용됩니다.");
            public static GUIContent cameraType = new GUIContent("Camera Type", "\"카메라 타입에 따라 지원되는 기능이 조금씩 다릅니다. 현재 Game 카메라만 포스트 프로세스가 지원되지만 알파 채널 분리가 안됩니다. 다른 카메라들은 포스트 프로세스가 지원되지 않지만 알파채널이 분리됩니다.\"");
            public static GUIContent reframeToTarget = new GUIContent("Reframe Target", "모델을 생성할 때 자동으로 뷰에 꽉 차도록 카메라의 거리를 조절합니다.");
            public static GUIContent recalculateBound = new GUIContent("Recalculate Bound", "모델을 생성할 때 바운딩 박스를 재계산합니다. Reframe 은 바운딩 박스에 기초합니다.");
            public static GUIContent forceUpdateComponent = new GUIContent("Force Update Components", "모델에 추가되어 있는 컴포넌트들을 강제로 실행합니다.");

            public static Texture2D logoTexture => Initializer.logoTexture;


            public class Tooltip
            {
                public static string createMode = "모델을 생성하는 방법을 선택합니다.";
            }
        }

        #endregion

        #region GUI

        void OnGUI_Top(Rect r)
        {
            if (IsDocked())
                EditorGUI.DrawRect(r, GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f);
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
                    //bool isPreview = settings.current.modelCreateMode == ModelCreateMode.Preview;
                    //using (EditorHelper.Colorize.Do(Color.white, isPreview ? Color.cyan : Color.white))
                    //{
                    //    if (GUILayout.Toggle(isPreview, "Preview", EditorStyles.toolbarButton))
                    //    {
                    //        settings.current.modelCreateMode = ModelCreateMode.Preview;
                    //    }
                    //}
                    using (EditorHelper.Colorize.Do(Color.white, Color.cyan))
                    {
                        if (GUILayout.Button("Render", EditorStyles.toolbarButton))
                        {
                            RenderAndSaveFile();
                        }
                    }
                    if (GUILayout.Button("Size", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Add Current"), false,
                            () => { AddViewportSize(_viewPortRect.size); });
                        menu.AddSeparator("");
                        for (var i = 0; i < dataManager.current.viewportSizes.Count; i++)
                        {
                            var size = dataManager.current.viewportSizes[i];
                            menu.AddItem(new GUIContent(string.Format("{0}x{1}", size.x, size.y)), false,
                                x => { ResizeWindow((Vector2)x); }, size);
                        }

                        menu.ShowAsContext();
                    }

                    if (GUILayout.Button("View", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Add Current"), false,
                            () =>
                            {
                                currentData.viewList.Add(new View(_destRot, _destDistance, _destPivotPos,
                                    _preview.cameraFieldOfView));
                            });
                        menu.AddSeparator("");
                        for (var i = 0; i < dataManager.current.viewList.Count; i++)
                        {
                            var view = dataManager.current.viewList[i];
                            menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), view.name)), false,
                                x => { ApplyView(x as View); }, view);
                        }

                        menu.ShowAsContext();
                    }

                    if (GUILayout.Button("Lighting", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Add Current"), false,
                            () => { currentData.lightingList.Add(GetCurrentLighting()); });
                        menu.AddSeparator("");
                        for (var i = 0; i < dataManager.current.lightingList.Count; i++)
                        {
                            var lighting = dataManager.current.lightingList[i];
                            menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), lighting.name)), false,
                                x => { ApplyLighting(x as Lighting); }, lighting);
                        }

                        menu.ShowAsContext();
                    }

                    if (GUILayout.Button("Model", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Pick"), false,
                            () =>
                            {
                                int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                                EditorGUIUtility.ShowObjectPicker<GameObject>(null, false, string.Empty, currentPickerWindow);
                            });
                        menu.AddSeparator("");
                        for (var i = 0; i < _recentModel.size; i++)
                        {
                            var recent = _recentModel.Get(i);
                            if (recent)
                            {
                                menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                                    x => { AddModel(x as GameObject); }, recent);
                            }
                        }
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Clear"), false,
                            () =>
                            {
                                foreach (var target in _targetDic.ToArray())
                                {
                                    if (target.Key)
                                    {
                                        RemoveModel(target.Value);
                                    }
                                }
                            });
                        menu.ShowAsContext();
                    }
                    if (GUILayout.Button("Animation", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Pick"), false,
                            () =>
                            {
                                int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                                EditorGUIUtility.ShowObjectPicker<AnimationClip>(null, false, string.Empty, currentPickerWindow);
                            });
                        menu.AddSeparator("");
                        for (var i = 0; i < _recentAnimation.size; i++)
                        {
                            var recent = _recentAnimation.Get(i);
                            if (recent)
                            {
                                menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                                    x => { AddAnimationAndPlay(x as AnimationClip); }, recent);
                            }
                        }

                        menu.ShowAsContext();
                    }
                    if (GUILayout.Button("Post Process", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Pick"), false,
                            () =>
                            {
                                int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                                if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                                {
#if UNITY_POST_PROCESSING_STACK_V2
                                    EditorGUIUtility.ShowObjectPicker<PostProcessProfile>(null, false, string.Empty, currentPickerWindow);
#endif
                                }
                                else
                                {
#if URP || HDRP
                                    EditorGUIUtility.ShowObjectPicker<VolumeProfile>(null, false, string.Empty, currentPickerWindow);
#endif
                                }
                            });
                        menu.AddSeparator("");

                        if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                        {
#if UNITY_POST_PROCESSING_STACK_V2
                            for (var i = 0; i < _recentPostProcessProfile.size; i++)
                            {
                                var recent = _recentPostProcessProfile.Get(i);
                                if (recent)
                                {
                                    menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                                        x => { SetPostProcessProfile((PostProcessProfile)x); }, recent);
                                }
                            }
#endif
                        }
                        else
                        {
#if URP || HDRP
                            for (var i = 0; i < _recentVolumeProfile.size; i++)
                            {
                                var recent = _recentVolumeProfile.Get(i);
                                if (recent)
                                {
                                    menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), recent.name)), false,
                                        x => { SetVolumeProfile((VolumeProfile)x); }, recent);
                                }
                            }
#endif
                        }
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Clear"), false, () =>
                        {

#if UNITY_POST_PROCESSING_STACK_V2
                            dataManager.current.profile = null;
#endif
#if URP || HDRP
                            dataManager.current.volumeProfile = null;
#endif
                            InitializePostProcess();
                        });
                        menu.ShowAsContext();
                    }
                    if (GUILayout.Button("Pipeline", EditorStyles.toolbarDropDown))
                    {
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Pick"), false,
                            () =>
                            {
#if URP || HDRP
                                int currentPickerWindow = EditorGUIUtility.GetControlID(FocusType.Passive);
                                EditorGUIUtility.ShowObjectPicker<RenderPipelineAsset>(null, false, string.Empty, currentPickerWindow);
#endif
                            });
                        menu.AddSeparator("");

#if URP || HDRP
                        var pipelines = AssetDatabase.FindAssets("t:RenderPipelineAsset").Select(x => AssetDatabase.GUIDToAssetPath(x)).ToList();
                        for (var i = 0; i < pipelines.Count; i++)
                        {
                            var pipeline = (RenderPipelineAsset)AssetDatabase.LoadAssetAtPath(pipelines[i], typeof(RenderPipelineAsset));
                            if (pipeline)
                            {
                                menu.AddItem(new GUIContent(string.Format("{0}.{1}", i.ToString(), pipeline.name)), false,
                                    x =>
                                    {
                                        dataManager.current.renderPipelineAsset = ((RenderPipelineAsset)x);
                                        InitializePipeline();
                                    }, pipeline);
                            }
                        }
#endif
                        menu.AddSeparator("");
                        menu.AddItem(new GUIContent("Builtin"), false, () =>
                        {
                            dataManager.current.renderPipelineAsset = null;
                            InitializePipeline();
                        });
                        menu.ShowAsContext();
                    }
                    //Handle Picker
                    if (Event.current.commandName == "ObjectSelectorUpdated")
                    {
                        var model = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                        if (model)
                        {
                            AddModel(model);
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                        }

                        var animation = EditorGUIUtility.GetObjectPickerObject() as AnimationClip;
                        if (animation)
                        {
                            AddAnimationAndPlay(animation);
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                        }
#if UNITY_POST_PROCESSING_STACK_V2
                        var postProfile = EditorGUIUtility.GetObjectPickerObject() as PostProcessProfile;
                        if (postProfile)
                        {
                            SetPostProcessProfile(postProfile);
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

                        }
#endif
#if URP || HDRP
                        var volumeProfile = EditorGUIUtility.GetObjectPickerObject() as VolumeProfile;
                        if (volumeProfile)
                        {
                            SetVolumeProfile(volumeProfile);
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                        }
                        var pipeline = EditorGUIUtility.GetObjectPickerObject() as RenderPipelineAsset;
                        if (pipeline)
                        {
                            dataManager.current.renderPipelineAsset = pipeline;
                            InitializePipeline();
                            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                        }
#endif
                    }
                    GUILayout.FlexibleSpace();
                    DataManager.OnManageGUI();
                }
            }
        }


        void OnGUI_Bottom(Rect r)
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

        void OnGUI_Viewport(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (r.size.x < 0 || r.size.y < 0) return;
            if (!_preview.camera.gameObject.activeInHierarchy) return;
            Rect renderRectScaled = new Rect(r.position, r.size);
            GUIStyle style = GUIStyle.none;
            bool enableSRP = currentData.renderPipelineMode != RenderPipelineMode.BuiltIn;
            using (new RenderPipelineOverrider(currentData.renderPipelineAsset))
            {
                using (new QualitySettingsOverrider())
                {
                    //using (new ShowObjectScope(_shadowGo))
                    //{
                    _preview.BeginPreview(renderRectScaled, style);
                    using (new RenderSettingsOverrider(AmbientMode.Flat, currentData.ambientSkyColor, _skyMaterial))
                    {
                        //GL.wireframe = true;
                        //_preview.DrawMesh(Grid.Get(100), Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one),
                        //    _gridMaterial, 0);
                        //GL.wireframe = false;
                        _preview.Render(enableSRP, _updateFOV);
                    }

                    //Texture image = _preview.EndPreview();
                    //GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                    //GUI.DrawTexture(r, image, ScaleMode.StretchToFill, true);
                    //GL.sRGBWrite = false;
                    //UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    _preview.EndAndDrawPreview(_viewPortRect);
                    //}
                }
            }
            //Grid.Draw(_preview.camera, 100, Color.white);
        }

        void OnGUI_Left(Rect r)
        {
            if (IsDocked())
                EditorGUI.DrawRect(r, GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f);
            //Open Settings Button
            GUIStyle style = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
            style.normal.textColor = GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f;
            Rect btnRect = new Rect(r.width, r.y, 24, r.height);
            string btn = _rs.openLeft.target ? "◄" : "►";
            EditorGUI.DropShadowLabel(btnRect, btn, style);
            _rs.openLeft.target = GUI.Toggle(btnRect, _rs.openLeft.target, btn, style);

            Rect area = new RectOffset(0, 0, 0, 0).Remove(r);
            using (new GUILayout.AreaScope(area))
            {
                Rect top = new Rect(area.x, area.y, area.width, EditorGUIUtility.singleLineHeight);
                //EditorGUI.LabelField(top, "Preview Hierachy", EditorStyles.toolbarButton);
                if (GUI.Button(top, "Unlock Inspector", EditorStyles.toolbarButton))
                {
                    UnlockInspector();
                }

                using (var svScope = new GUILayout.ScrollViewScope(_scrollPos1))
                {
                    _scrollPos1 = svScope.scrollPosition;

                    area.y += EditorGUIUtility.singleLineHeight + 4;

                    if (_treeView != null)
                    {
                        _treeView.OnGUI(area);
                    }
                }
            }
        }

        void OnGUI_TreeView(Rect r)
        {
            Rect area = new RectOffset(2, 2, 2, 2).Remove(r);
            using (new GUILayout.AreaScope(area))
            {
                //if (GUILayout.Button("Unlock Inspector", EditorStyles.miniButton))
                //{
                //    UnlockObject();
                //}

                using (var svScope = new GUILayout.ScrollViewScope(_scrollPos1))
                {
                    _scrollPos1 = svScope.scrollPosition;
                    EditorGUI.LabelField(r, "Preview Hierachy", EditorStyles.largeLabel);
                    area.y += EditorGUIUtility.singleLineHeight + 4;

                    if (_treeView != null)
                    {
                        _treeView.OnGUI(area);
                    }
                }
            }
        }

        void OnGUI_Right(Rect r)
        {
            if (IsDocked())
                EditorGUI.DrawRect(r, GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f);
            //Open Settings Button
            GUIStyle style = new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
            style.normal.textColor = GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f;
            Rect btnRect = new Rect(r.x - 24, r.y, 24, r.height);
            string btn = _rs.openRight.target ? "►" : "◄";
            EditorGUI.DropShadowLabel(btnRect, btn, style);
            _rs.openRight.target = GUI.Toggle(btnRect, _rs.openRight.target, btn, style);
            Rect area = new RectOffset(0, 0, 0, 0).Remove(r);

            using (EditorHelper.Fade.Do(_rs.openRight.faded))
            {
                using (new GUILayout.AreaScope(area))
                {
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        panelMode = (SidePanelMode)GUILayout.Toolbar((int)panelMode,
                            Enum.GetNames(typeof(SidePanelMode)), EditorStyles.toolbarButton);
                        if (check.changed)
                        {
                        }
                    }

                    using (var svScope = new GUILayout.ScrollViewScope(_scrollPos))
                    {
                        _scrollPos = svScope.scrollPosition;
                        switch (panelMode)
                        {
                            case SidePanelMode.View:
                                OnGUI_View();
                                break;
                            case SidePanelMode.Model:
                                OnGUI_Model();
                                break;
                            case SidePanelMode.Animation:
                                OnGUI_Animation();
                                break;
                            case SidePanelMode.Misc:
                                OnGUI_Misc();
                                break;
                        }
                    }
                }
            }
        }

        void OnGUI_View()
        {
            EditorHelper.IconLabel(typeof(Camera), "View");

            EditorHelper.FoldGroup.Do("Control", true, () =>
            {
                currentData.rotSpeed = EditorGUILayout.IntSlider("Rotate Speed", currentData.rotSpeed, 1, 5);
                currentData.zoomSpeed = EditorGUILayout.IntSlider("Zoom Speed", currentData.zoomSpeed, 1, 5);
                currentData.panSpeed = EditorGUILayout.IntSlider("Pan Speed", currentData.panSpeed, 1, 5);
                currentData.smoothFactor = EditorGUILayout.IntSlider("Smoothness", currentData.smoothFactor, 1, 5);
                _destPivotPos = EditorGUILayout.Vector3Field("Focus", _destPivotPos);
                _targetOffset = EditorGUILayout.Vector3Field("Offset", _targetOffset);
                using (EditorHelper.Horizontal.Do())
                {
                    _autoRotateCamera = GUILayout.Toggle(_autoRotateCamera, "Rotate Camera",
                        EditorStyles.miniButton,
                        GUILayout.Width(_labelWidth));
                    _cameraAutoRotationSpeed = EditorGUILayout.IntSlider(_cameraAutoRotationSpeed, -10, 10);
                }

                using (EditorHelper.Horizontal.Do())
                {
                    _autoRotateLight = GUILayout.Toggle(_autoRotateLight, "Rotate Light", EditorStyles.miniButton,
                        GUILayout.Width(_labelWidth));
                    _lightAutoRotationSpeed = EditorGUILayout.IntSlider(_lightAutoRotationSpeed, -10, 10);
                }
            });

            EditorHelper.FoldGroup.Do("Size", true, () =>
            {
                using (EditorHelper.Horizontal.Do())
                {
                    if (GUILayout.Button("New", EditorStyles.miniButtonLeft))
                    {
                        ShowPopupWindow();
                    }

                    if (GUILayout.Button("Add Current", EditorStyles.miniButtonRight))
                    {
                        AddViewportSize(_viewPortRect.size);
                    }
                }

                EditorHelper.GridLayout(currentData.viewportSizes.Count, 2, (i) =>
                {
                    if (i < 0 || i > currentData.viewportSizes.Count - 1) return;
                    var size = currentData.viewportSizes[i];
                    if (GUILayout.Button(string.Format("{0}x{1}", size.x.ToString("#"), size.y.ToString("#")),
                        EditorStyles.miniButtonLeft, GUILayout.MaxWidth(90)))
                    {
                        ResizeWindow(size);
                    }

                    if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
                    {
                        currentData.viewportSizes.Remove(size);
                    }
                });

            });

            EditorHelper.FoldGroup.Do("Image", true, () =>
            {
                //GUILayout.Label(string.Format("Name : {0}"), EditorStyles.miniLabel);
                GUILayout.Label(
                    string.Format("Size : {0} x {1}", _viewPortRect.width * currentData.imageSizeMultiplier,
                        _viewPortRect.height * currentData.imageSizeMultiplier), EditorStyles.miniLabel);
                using (EditorHelper.Horizontal.Do())
                {
                    currentData.imageSizeMultiplier = EditorGUILayout.IntSlider(currentData.imageSizeMultiplier, 1, 8);
                    currentData.alphaAppliedImage =
                        GUILayout.Toggle(currentData.alphaAppliedImage, "Alpha", EditorStyles.miniButton, GUILayout.Width(60));
                }

                using (EditorHelper.Horizontal.Do())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (EditorHelper.Horizontal.Do())
                        {
                            currentData.imageSaveMode = (ImageSaveMode)GUILayout.Toolbar(
                                (int)currentData.imageSaveMode, Enum.GetNames(typeof(ImageSaveMode)),
                                EditorStyles.miniButton);
                        }

                        currentData.openSavedImage = GUILayout.Toggle(currentData.openSavedImage, "Open Saved Image",
                            EditorStyles.miniButton);
                        if (GUILayout.Button("Open Saved Folder", EditorStyles.miniButton))
                        {
                            EditorUtility.OpenWithDefaultApp(Directory.GetParent(Application.dataPath).ToString() +
                                                             "/Screenshots");
                        }
                    }

                    using (EditorHelper.Colorize.Do(Color.white, Color.cyan))
                    {
                        if (GUILayout.Button("Render", GUILayout.Width(60), GUILayout.Height(48)))
                        {
                            RenderAndSaveFile();
                        }
                    }
                }

                if (!EditorUserBuildSettings.activeBuildTarget.ToString().Contains("Standalone") &&
                    currentData.alphaAppliedImage && currentData.postProcessEnabled)
                {
                    EditorGUILayout.HelpBox("Only standalone platforms supports alpha blended post process ",
                        MessageType.Warning);
                }

            });

            EditorHelper.FoldGroup.Do("View", true, () =>
            {
                //_targetOffset = EditorGUILayout.Vector3Field("Target Offset", _targetOffset);
                using (EditorHelper.Horizontal.Do())
                {
                    if (GUILayout.Button("Front", EditorStyles.miniButtonLeft))
                    {
                        _destRot = new Vector2(180, 0);
                    }

                    if (GUILayout.Button("Left", EditorStyles.miniButtonMid))
                    {
                        _destRot = new Vector2(90, 0);
                    }

                    if (GUILayout.Button("Back", EditorStyles.miniButtonMid))
                    {
                        _destRot = Vector2.zero;
                    }

                    if (GUILayout.Button("Right", EditorStyles.miniButtonMid))
                    {
                        _destRot = new Vector2(-90, 0);
                    }

                    if (GUILayout.Button("Top", EditorStyles.miniButtonMid))
                    {
                        _destRot = new Vector2(180, 90);
                    }

                    if (GUILayout.Button("Bottom", EditorStyles.miniButtonRight))
                    {
                        _destRot = new Vector2(180, -90);
                    }
                }

                using (EditorHelper.Horizontal.Do())
                {
                    using (EditorHelper.LabelWidth.Do(80))
                    {
                        _destFOV = EditorGUILayout.IntSlider("Field Of View", (int)_destFOV, 1, 179);
                        _preview.camera.orthographic = GUILayout.Toggle(_preview.camera.orthographic,
                            _preview.camera.orthographic ? "O" : "P", EditorStyles.miniButton, GUILayout.Width(20));
                    }
                }

                using (EditorHelper.Horizontal.Do())
                {
                    if (GUILayout.Button("Add Current", EditorStyles.miniButtonLeft))
                    {
                        currentData.viewList.Add(new View(_destRot, _destDistance, _destPivotPos,
                            _preview.cameraFieldOfView));
                    }

                    if (GUILayout.Button("From Scene", EditorStyles.miniButtonRight))
                    {
                        var menu = new GenericMenu();
                        var cameras = FindAllObjectsInScene().SelectMany(x => x.GetComponentsInChildren<Camera>())
                            .ToArray(); //Find Inactive
                        for (var i = 0; i < cameras.Length; i++)
                        {
                            var cam = cameras[i];
                            menu.AddItem(new GUIContent(string.Format("{0}", cam.name)), false,
                                x =>
                                {
                                    var view = new View((Camera)x);
                                    currentData.viewList.Add(view);
                                    ApplyView(view);
                                }, cam);
                        }

                        menu.ShowAsContext();
                    }
                }

                EditorHelper.GridLayout(currentData.viewList.Count, 2, (i) =>
                {
                    if (i < 0 || i > currentData.viewList.Count - 1) return;
                    var view = currentData.viewList[i];
                    if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(20)))
                    {
                        view.rotation = _destRot;
                        view.distance = _destDistance;
                        view.pivot = _destPivotPos;
                        view.fieldOfView = _preview.cameraFieldOfView;
                        Notice.Log(string.Format("Current view saved to slot {0}", i.ToString()), false);
                    }

                    if (GUILayout.Button(string.Format("{0}.{1}", i.ToString(), view.name), EditorStyles.miniButtonMid,
                        GUILayout.MaxWidth(70)))
                    {
                        ApplyView(i);
                    }

                    if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(20)))
                    {
                        currentData.viewList.Remove(view);
                        Notice.Log(string.Format("Slot {0} Removed", i.ToString()), false);
                    }
                });
            });

            EditorHelper.FoldGroup.Do("Environment", true, () =>
            {
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    using (EditorHelper.Horizontal.Do())
                    {
                        EditorGUILayout.PrefixLabel("Background");
                        bool isSky = (currentData.clearFlag == ClearFlags.Sky);
                        isSky = !GUILayout.Toggle(!isSky, "Color", EditorStyles.miniButtonLeft);
                        isSky = GUILayout.Toggle(isSky, "Environment", EditorStyles.miniButtonRight);
                        currentData.clearFlag = isSky ? ClearFlags.Sky : ClearFlags.Color;
                    }
                    currentData.bgColor = EditorGUILayout.ColorField(new GUIContent("Color"), currentData.bgColor, false, false, false);
                    _skyMaterial = (Material)EditorGUILayout.ObjectField("Sky Material", _skyMaterial, typeof(Material), false);

                    currentData.cubeMap = (Cubemap)EditorGUILayout.ObjectField("Environment", currentData.cubeMap, typeof(Cubemap), false);
                    currentData.CubeMapMipMapBias = EditorGUILayout.IntSlider("Bias", (int)currentData.CubeMapMipMapBias, 0, 10);
                    currentData.autoFloorHeightEnabled = GUILayout.Toggle(currentData.autoFloorHeightEnabled, "Auto Floor Height", EditorStyles.miniButton);
                    currentData.floorHeight = EditorGUILayout.Slider("Floor Height", currentData.floorHeight, -10f, 10f);
                    currentData.floorScale = EditorGUILayout.Slider("Floor Scale", currentData.floorScale, 0f, 100f);

                    if (check.changed)
                    {
                        _preview.camera.backgroundColor = currentData.bgColor;
                        _preview.camera.clearFlags = CameraClearFlags.SolidColor;
                        _preview.camera.clearFlags = CameraClearFlags.Skybox;
                        ApplyBackground();
                        ApplyReflectionEnvironment();
                    }
                }
            });

            EditorHelper.FoldGroup.Do("Lighting", true, () =>
            {
                using (var lightCheck = new EditorGUI.ChangeCheckScope())
                {
                    using (EditorHelper.LabelWidth.Do(80))
                    {
                        _preview.ambientColor = currentData.ambientSkyColor = EditorGUILayout.ColorField(new GUIContent("Ambient"), currentData.ambientSkyColor, false, false, true);
                    }
                    _lightRotationIndex = GUILayout.Toggle(_lightRotationIndex==0, "Rotate MainLight Only",EditorStyles.miniButtonLeft) ? 0 : 1;
                    for (var i = 0; i < _preview.lights.Length; i++)
                    {
                        var previewLight = _preview.lights[i];
                        using (EditorHelper.Horizontal.Do())
                        {
                            using (EditorHelper.LabelWidth.Do(40))
                            {
                                GUILayout.Label(string.Format("Light{0}", i.ToString()), EditorStyles.miniLabel);
                                previewLight.color = EditorGUILayout.ColorField(new GUIContent(""), previewLight.color, true, true, false, GUILayout.Width(50));
                                previewLight.intensity = EditorGUILayout.Slider("", previewLight.intensity, 0, 2);
                                EditorGUIUtility.labelWidth = _labelWidth;
                            }

                            if (lightCheck.changed)
                            {

                            }
                        }
                    }

                    using (EditorHelper.Horizontal.Do())
                    {
                        if (GUILayout.Button("Add Current", EditorStyles.miniButtonLeft))
                        {
                            var lighting = GetCurrentLighting();
                            currentData.lightingList.Add(lighting);
                            ApplyLighting(lighting);
                        }

                        if (GUILayout.Button("From Scene", EditorStyles.miniButtonRight))
                        {
                            var lighting = new Lighting();
                            lighting.ambientSkyColor = RenderSettings.ambientSkyColor;
                            var lights = FindObjectsOfType<Light>(); //Not Find Inactive
                            foreach (var light in lights)
                            {
                                var info = new Lighting.LightInfo();
                                info.position = light.transform.position;
                                info.rotation = light.transform.rotation;
                                info.lightColor = light.color;
                                info.intensity = light.intensity;
                                lighting.lightList.Add(info);
                            }

                            currentData.lightingList.Add(lighting);
                            ApplyLighting(lighting);
                        }
                    }

                    EditorHelper.GridLayout(currentData.lightingList.Count, 2, (i) =>
                    {
                        if (i < 0 || i > currentData.lightingList.Count - 1) return;
                        var lighting = currentData.lightingList[i];
                        if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(20)))
                        {
                            lighting.ambientSkyColor = dataManager.current.ambientSkyColor;
                            lighting.lightList.Clear();
                            foreach (var light in _preview.lights)
                            {
                                var info = new Lighting.LightInfo();
                                info.position = light.transform.position;
                                info.rotation = light.transform.rotation;
                                info.lightColor = light.color;
                                info.intensity = light.intensity;
                                lighting.lightList.Add(info);
                            }

                            Notice.Log(string.Format("Current view saved to slot {0}", i.ToString()), false);
                        }

                        if (GUILayout.Button(string.Format("{0}.{1}", i.ToString(), lighting.name),
                            EditorStyles.miniButtonMid, GUILayout.MaxWidth(70)))
                        {
                            ApplyLighting(lighting);
                        }

                        if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(20)))
                        {
                            currentData.lightingList.Remove(lighting);
                            Notice.Log(string.Format("Slot {0} Removed", i.ToString()), false);
                        }
                    });
                }
            });
            EditorHelper.FoldGroup.Do("Shadow", true, () =>
            {
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    currentData.shadowEnabled = GUILayout.Toggle(currentData.shadowEnabled, "Enable Shadow", EditorStyles.miniButton);
                    currentData.shadowStrength = EditorGUILayout.Slider("Stregth", currentData.shadowStrength, 0f, 1f);
                    currentData.shadowBias = EditorGUILayout.Slider("Bias", currentData.shadowBias, 0f, 1f);
                    if (check.changed)
                    {
                        for (var i = 0; i < _preview.lights.Length; i++)
                        {
                            var previewLight = _preview.lights[i];
                            previewLight.shadows = currentData.shadowEnabled ? LightShadows.Soft : LightShadows.None;
                            previewLight.shadowStrength = currentData.shadowStrength;
                            previewLight.shadowBias = currentData.shadowBias;
                        }
                    }
                }
            });
            EditorHelper.FoldGroup.Do("Render", true, () =>
            {
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    currentData.cameraType = (CameraType)EditorGUILayout.EnumPopup(GUIContents.cameraType.text, currentData.cameraType);
                    Tooltip.Generate(GUIContents.cameraType.tooltip);
                    if (check.changed)
                    {
                        _preview.camera.cameraType = currentData.cameraType;
                    }
                }
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    currentData.renderPipelineAsset = (RenderPipelineAsset)EditorGUILayout.ObjectField(GUIContents.currentPipeline.text, currentData.renderPipelineAsset, typeof(RenderPipelineAsset), false);
                    Tooltip.Generate(GUIContents.currentPipeline.tooltip);
                    if (check.changed)
                    {
                        onChangeRenderPipeline?.Invoke();
                        Debug.Log("Pipeline Asset Chaned");
                    }
                }



                
            });
            // Builtin Pipeline Only Menu
            if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
            {
                EditorHelper.FoldGroup.Do("Builtin RP", true, () =>
                {
                    bool wireFrameEnabled = _wireFrameEnabled;
                    bool colorEnabled = _colorEnabled;
                    bool heightFogEnabled = currentData.heightFogEnabled;
                    bool shadowEnabled = currentData.planeShadowEnabled;
                    using (EditorHelper.Horizontal.Do())
                    {
                        _colorEnabled = GUILayout.Toggle(_colorEnabled, "Color", EditorStyles.miniButton,
                            GUILayout.Width(80));
                        //SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _colorCommandBuffer, _colorMaterial, _colorEnabled);
                        _color = EditorGUILayout.ColorField(_color);
                    }

                    using (EditorHelper.Horizontal.Do())
                    {
                        _wireFrameEnabled = GUILayout.Toggle(_wireFrameEnabled, "WireFrame", EditorStyles.miniButton,
                            GUILayout.Width(80));
                        //SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _wireCommandBuffer, _wireMaterial, _wireFrameEnabled);
                        currentData.wireLineColor = EditorGUILayout.ColorField(currentData.wireLineColor);
                    }

                    using (EditorHelper.Horizontal.Do())
                    {
                        currentData.planeShadowEnabled = GUILayout.Toggle(currentData.planeShadowEnabled, "PlaneShadow",
                            EditorStyles.miniButton, GUILayout.Width(80));
                        //SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _shadowCommandBuffer, _shadowMaterial, currentData.enablePlaneShadows);
                        currentData.planeShadowColor = EditorGUILayout.ColorField(currentData.planeShadowColor);
                    }

                    using (EditorHelper.Horizontal.Do())
                    {
                        currentData.heightFogEnabled = GUILayout.Toggle(currentData.heightFogEnabled, "Height Fog",
                            EditorStyles.miniButton, GUILayout.Width(80));
                        //SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _heightFogCommandBuffer, _heightFogMaterial, currentData.enableHeightFog);
                        currentData.heightFogHeight = GUILayout.HorizontalSlider(currentData.heightFogHeight,
                            _targetInfo.bounds.min.y + 0.0001f, _targetInfo.bounds.size.y);
                        currentData.heightFogColor =
                            EditorGUILayout.ColorField(currentData.heightFogColor, GUILayout.Width(60));
                    }

                    if (wireFrameEnabled != _wireFrameEnabled ||
                        colorEnabled != _colorEnabled ||
                        shadowEnabled != currentData.planeShadowEnabled ||
                        heightFogEnabled != currentData.heightFogEnabled)
                    {
                        ApplyModelCommandBuffers();
                    }

                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        using (EditorHelper.LabelWidth.Do(60))
                        {
                            using (EditorHelper.Horizontal.Do())
                            {
                                replaceMentShader = (Shader)EditorGUILayout.ObjectField("Shader", replaceMentShader,
                                    typeof(Shader), false);
                                if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(40)))
                                {
                                    replaceMentShader = null;
                                    _preview.camera.ResetReplacementShader();
                                }
                            }
                        }

                        if (check.changed)
                        {
                            if (replaceMentShader)
                            {
                                _preview.camera.SetReplacementShader(replaceMentShader, "");

                            }
                            else
                            {
                                _preview.camera.ResetReplacementShader();
                            }
                        }
                    }

                    using (EditorHelper.Horizontal.Do())
                    {
                        using (var check = new EditorGUI.ChangeCheckScope())
                        {
                            _gridEnabled = GUILayout.Toggle(_gridEnabled, "Grid", EditorStyles.miniButton,
                                GUILayout.Width(80));
                            if (check.changed)
                            {
                                //_gridSize = EditorGUILayout.IntSlider(_gridSize, 0, 100);
                                SetGridBuffer(_gridEnabled);
                            }
                        }

                        _gridColor = EditorGUILayout.ColorField(_gridColor);
                    }

                    using (EditorHelper.Horizontal.Do())
                    {
                        using (var check = new EditorGUI.ChangeCheckScope())
                        {
                            _viewMode = (ViewMode)GUILayout.Toolbar((int)_viewMode, Enum.GetNames(typeof(ViewMode)),
                                EditorStyles.miniButton);
                            if (check.changed)
                            {
                                ApplyCameraCommandBuffers();
                            }
                        }
                    }
                    _screenSeparate = EditorGUILayout.Slider("Separate", _screenSeparate, 0, 1);
                });
            }
#if URP
            if (currentData.renderPipelineMode == RenderPipelineMode.Universal)
            {
                EditorHelper.FoldGroup.Do("Universal RP", true, () =>
                {
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        currentData.renderScale = EditorGUILayout.Slider("Render Scale", currentData.renderScale, 0f, 2f);
                        currentData.urpData.antialiasing = EditorGUILayout.IntPopup(currentData.urpData.antialiasing, Enum.GetNames(typeof(AntialiasingMode)), (int[])Enum.GetValues(typeof(AntialiasingMode)), EditorStyles.miniButton);
                        currentData.urpData.dithering = GUILayout.Toggle(currentData.urpData.dithering, "Enable Dithering", EditorStyles.miniButton);
                        if (check.changed)
                        {
                            UniversalRenderPipelineAsset urpPipelineAsset = currentData.renderPipelineAsset as UniversalRenderPipelineAsset;
                            urpPipelineAsset.renderScale = currentData.renderScale;
                            _urpCamera.antialiasing = (AntialiasingMode)currentData.urpData.antialiasing;
                            _urpCamera.dithering = currentData.urpData.dithering;
                        }
                    }
                });
            }
#endif
#if HDRP
            if (currentData.renderPipelineMode == RenderPipelineMode.HighDefinition)
            {
                EditorHelper.FoldGroup.Do("HDRP", true, () =>
                {
                    GUILayout.Label("High Definition Render Pipeline", EditorStyles.miniBoldLabel);
                    EditorGUILayout.HelpBox("Not Implemented Yet", MessageType.Error);
                    currentData.hdrpData.antialiasing = EditorGUILayout.IntPopup(currentData.hdrpData.antialiasing, Enum.GetNames(typeof(HDAdditionalCameraData.AntialiasingMode)), (int[])Enum.GetValues(typeof(HDAdditionalCameraData.AntialiasingMode)), EditorStyles.miniButton);
                    _hdrpCamera.antialiasing = (HDAdditionalCameraData.AntialiasingMode)currentData.hdrpData.antialiasing;
                    _hdrpCamera.dithering = currentData.hdrpData.dithering = GUILayout.Toggle(currentData.hdrpData.dithering, "Enable Dithering", EditorStyles.miniButton);
                });
            }
#endif          

            EditorHelper.FoldGroup.Do("Post Process", true, () =>
            {

                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    currentData.postProcessEnabled = GUILayout.Toggle(currentData.postProcessEnabled, "Enable Post Processing", EditorStyles.miniButton);
                    if (currentData.renderPipelineMode == RenderPipelineMode.BuiltIn)
                    {
#if UNITY_POST_PROCESSING_STACK_V2
                        currentData.profile = (PostProcessProfile)EditorGUILayout.ObjectField("", currentData.profile, typeof(PostProcessProfile), false);
#else
                        EditorGUILayout.HelpBox("No Post Processing Feature found.", MessageType.None);
#endif
                    }
                    else
                    {
#if URP || HDRP
                        currentData.volumeProfile = (VolumeProfile)EditorGUILayout.ObjectField("", currentData.volumeProfile, typeof(VolumeProfile), false);
#endif
                    }

                    if (check.changed)
                    {
                        InitializePostProcess();
#if UNITY_POST_PROCESSING_STACK_V2
                        _recentPostProcessProfile.Add(AssetDatabase.GetAssetPath(currentData.profile));
#endif
#if URP || HDRP
                        _recentVolumeProfile.Add(AssetDatabase.GetAssetPath(currentData.volumeProfile));
#endif
                    }
                }
            });

            EditorHelper.FoldGroup.Do("Gizmos", true, () =>
            {
                using (EditorHelper.Horizontal.Do())
                {
                    string[] enumNames = Enum.GetNames(_gizmoMode.GetType());
                    bool[] buttons = new bool[enumNames.Length];
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        _gizmoMode = GUILayout.Toggle((int)_gizmoMode == 0, "None", EditorStyles.miniButtonLeft)
                            ? 0
                            : _gizmoMode;
                        int buttonsValue = 0;
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            buttons[i] = ((int)_gizmoMode & (1 << i + 1)) == (1 << i + 1);
                            buttons[i] = GUILayout.Toggle(buttons[i], enumNames[i], EditorStyles.miniButtonMid);
                            if (buttons[i])
                            {
                                buttonsValue += 1 << i + 1;
                            }
                        }

                        if (check.changed)
                        {
                            _gizmoMode = (GizmoMode)buttonsValue;
                        }

                        //_gizmoMode = GUILayout.Toggle((int)_gizmoMode == ~0, "All", EditorStyles.miniButtonRight) ? (GizmoMode)~0 : _gizmoMode;
                        if (GUILayout.Button("All", EditorStyles.miniButtonRight))
                        {
                            _gizmoMode = (GizmoMode)~0;
                        }
                    }
                }
            });
        }


        void OnGUI_Model()
        {
            EditorHelper.IconLabel(typeof(Avatar), "Model");

            EditorHelper.FoldGroup.Do("Create Options", true, () =>
            {
                using (EditorHelper.Horizontal.Do())
                {
                    currentData.reframeToTarget = GUILayout.Toggle(currentData.reframeToTarget, GUIContents.reframeToTarget, EditorStyles.miniButtonLeft);
                    currentData.recalculateBound = GUILayout.Toggle(currentData.recalculateBound, GUIContents.recalculateBound, EditorStyles.miniButtonRight);
                }
                using (EditorHelper.Horizontal.Do())
                {
                    currentData.forceUpdateComponent = GUILayout.Toggle(currentData.forceUpdateComponent, GUIContents.forceUpdateComponent, EditorStyles.miniButton);
                }
            });

            EditorHelper.FoldGroup.Do("Create Mode", true, () =>
            {
                dataManager.current.modelCreateMode = (ModelCreateMode)GUILayout.Toolbar((int)dataManager.current.modelCreateMode, Enum.GetNames(typeof(ModelCreateMode)), "Button", GUILayout.Height(20));
                Tooltip.Generate(GUIContents.Tooltip.createMode);

                switch (dataManager.current.modelCreateMode)
                {
                    case ModelCreateMode.Default:
                        EditorGUILayout.HelpBox("Manually select the GameObject you want to create.", MessageType.None);
                        _tempObj = EditorGUILayout.ObjectField(_tempObj, typeof(GameObject), false) as GameObject;
                        Tooltip.Generate("생성할 모델을 선택하세요");
                        using (EditorHelper.Horizontal.Do())
                        {
                            using (EditorHelper.Vertical.Do())
                            {
                                if (GUILayout.Button("Primitives", EditorStyles.popup))
                                {
                                    var menu = new GenericMenu();
                                    menu.AddItem(new GUIContent("Sphere"), false, () =>
                                    {
                                        var primitive = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                                        primitive.GetComponent<Renderer>().material = DefaultMaterial.Get(currentData.renderPipelineMode);
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

                                if (GUILayout.Button("Extra Primitives", EditorStyles.popup))
                                {
                                }
                            }

                            if (GUILayout.Button("Create", GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                            {
                                if (_tempObj)
                                {
                                    AddModel(_tempObj);
                                }
                            }
                            Tooltip.Generate("모델을 생성합니다");


                        }
                        break;
                    case ModelCreateMode.Preview:
                        EditorGUILayout.HelpBox("GameObjects selected in the Project view are automatically created.", MessageType.None);
                        break;

                    case ModelCreateMode.Custom:
                        if (_customLoader != null)
                        {
                            _customLoader.OnGUI();
                        }
                        else
                        {
                          EditorGUILayout.HelpBox("No custom loader class found", MessageType.None);
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                if (EditorGUIUtility.GetObjectPickerControlID() != 0) //어떤 object picker 가 안 열렸을 때 0
                {
                    _tempPickedObject = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                    if (_tempObj != _tempPickedObject)
                    {
                        _tempObj = _tempPickedObject;
                        AddModel(_tempObj);
                        _tempPickedObject = null;
                    }
                }

                if (!(currentData.modelCreateMode == ModelCreateMode.Preview))
                {

                }
            });

            EditorHelper.FoldGroup.Do("Recent", true, () =>
            {
                if (_recentModel != null) _recentModel.OnGUI();
            });

            EditorHelper.FoldGroup.Do("Info", true, () =>
            {
                EditorGUILayout.HelpBox(_targetInfo.Print(), MessageType.None);


            });

            EditorHelper.FoldGroup.Do("Model", true, () =>
            {
                GUILayout.Label("MainTarget", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledGroupScope(true))
                {
                    EditorGUILayout.ObjectField("Instance", _mainTarget, typeof(GameObject), false);
                }
                GUILayout.Label("Target Dic", EditorStyles.boldLabel);
                foreach (var target in _targetDic.ToArray())
                {
                    GameObject source = target.Key;
                    GameObject instance = target.Value;
                    if (source != null)
                    {
                        using (EditorHelper.Horizontal.Do())
                        {
                            using (EditorHelper.Vertical.Do())
                            {
                                EditorGUILayout.ObjectField("Source", target.Key, typeof(GameObject), false);
                                EditorGUILayout.ObjectField("Instance", target.Value, typeof(GameObject), false);
                                if (GUILayout.Button("Remove", EditorStyles.miniButton))
                                {
                                    RemoveModel(target.Value);
                                }
                            }
                        }
                    }
                }
            });

            EditorHelper.FoldGroup.Do("Materials", true, () =>
            {
                using (EditorHelper.LabelWidth.Do(80))
                {
                    foreach (var renderer in _targetInfo.renderers)
                    {
                        if (renderer)
                        {
                            for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                            {
                                Material mat = renderer.sharedMaterials[i];
                                using (var check = new EditorGUI.ChangeCheckScope())
                                {
                                    var material = (Material)EditorGUILayout.ObjectField(renderer.name, mat, typeof(Material), false);
                                    if (check.changed)
                                    {
#if UNITY_2022_2_OR_NEWER
                                        List<Material> newMaterialList = renderer.sharedMaterials.ToList();
                                        newMaterialList[i] = material;
                                        renderer.SetSharedMaterials(newMaterialList);
#else
                                        List<Material> cachedMaterialList = new List<Material>();
                                        renderer.GetSharedMaterials(cachedMaterialList);
                                        cachedMaterialList[i] = material;
                                        renderer.sharedMaterials = cachedMaterialList.ToArray();
#endif
                                    }
                                }
                            }
                        }
                    }
                }
            });


            EditorHelper.FoldGroup.Do("Components", true, () =>
            {
                using (EditorHelper.LabelWidth.Do(80))
                {
                    foreach (var behaviour in _targetInfo.behaviours)
                    {
                        if (behaviour)
                        {
                            using (var check = new EditorGUI.ChangeCheckScope())
                            {
                                EditorGUILayout.ObjectField(behaviour.name, behaviour, typeof(MonoBehaviour), false);
                                if (check.changed)
                                {

                                }
                            }
                        }
                    }
                }
            });
        }

        void OnGUI_Animation()
        {
            EditorHelper.IconLabel(typeof(Animation), "Animation");
            for (int a = 0; a < _playerList.Count; a++)
            {
                var player = _playerList[a];
                EditorHelper.FoldGroup.Do(string.Format("{0} - {1}", "Player", a.ToString()), true, () =>
                {
                    for (int b = 0; b < player.actorList.Count; b++)
                    {
                        player.reorderableClipList.DoLayoutList();
                    }

                    EditorGUILayout.Space();
                });
                ////Drag and Drop
                Event evt = Event.current;
                Rect drop_area = _viewPortRect; //? 왜 뷰포트가 되고 right 는 안되는가?
                switch (evt.type)
                {
                    case EventType.DragUpdated:
                    case EventType.DragPerform:
                        if (!drop_area.Contains(evt.mousePosition))
                            return;
                        DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                        if (evt.type == EventType.DragPerform)
                        {
                            DragAndDrop.AcceptDrag();
                            foreach (Object dragged_object in DragAndDrop.objectReferences)
                            {
                                if (dragged_object is AnimationClip)
                                {
                                    var clip = dragged_object as AnimationClip;
                                    _playerList.FirstOrDefault().clipInfoList.Add(new AnimationPlayer.ClipInfo(clip));
                                }
                            }
                        }

                        break;
                }
            }

            EditorHelper.FoldGroup.Do("Transform Modifier", true, () =>
            {
                if (GUILayout.Button("Add Transfrom Modifier"))
                {
                    if (_playerList.Count > 0)
                    {
                        var player = _playerList[0];
                        player.AddModifierHandler();
                    }
                }
                if (_playerList.Count > 0)
                {
                    var player = _playerList[0];
                    for (int i = 0; i < player.modifierList.Count; i++)
                    {
                        TransformModifier modifier = player.modifierList[i];
                        if (GUILayout.Button("Remove"))
                        {
                            modifier.Reset();
                            player.modifierList.Remove(modifier);
                        }
                        modifier.OnGUI(true, true, true);
                    }

                }
            });
            EditorHelper.FoldGroup.Do("Recent", true, () =>
            {
                _recentAnimation.OnGUI();
            });
            EditorHelper.FoldGroup.Do("Steel", false, () =>
            {
                if (GUILayout.Button("Add Current"))
                {
                    if (_playerList.Count > 0)
                    {
                        var player = _playerList[0];
                        if (player.playList.Count > 0)
                        {
                            var steel = new Steel(player.currentClipInfo.clip, player.time);
                            currentData.steelList.Add(steel);
                        }
                    }
                }
                if (GUILayout.Button("Clear"))
                {
                    currentData.steelList.Clear();
                }
                for (int i = 0; i < currentData.steelList.Count; i++)
                {
                    Steel steel = currentData.steelList[i];
                    using (EditorHelper.Horizontal.Do())
                    {
                        if (GUILayout.Button(string.Format("{0}:{1}", steel.animationClip.name, steel.time.ToString("F")), EditorStyles.miniButton))
                        {
                            var player = _playerList[0];
                        }
                        if (GUILayout.Button("-", EditorStyles.miniButton, GUILayout.Width(30)))
                        {
                            currentData.steelList.Remove(steel);
                        }
                    }

                }
            });
        }



        void OnGUI_AnimationControl(Rect r)
        {
            if (!_overlayEnabled) return;
            Rect area = new RectOffset(0, 0, 0, 0).Remove(r);
            using (new GUILayout.AreaScope(area))
            {
                if (_playerList.Count == 0) return;
                //if (_player == null) return;
                foreach (var animationPlayer in _playerList)
                {
                    animationPlayer.OnGUI_Control();
                }
            }
        }

        void OnGUI_ParticleSystemControl(Rect r)
        {
            if (!_overlayEnabled) return;
            if (_targetInfo.particleSystems == null) return;
            if (_targetInfo.particleSystems.Count == 0) return;
            Rect area = new RectOffset(4, 4, 4, 4).Remove(r);
            //EditorGUI.DrawRect(psRect, GetInvertedLuminaceGrayscaleColor(_preview.camera.backgroundColor) * 0.5f);
            using (new GUILayout.AreaScope(area))
            {
                ParticleSystem particleSystem = _targetInfo.particleSystems[0];
                if (particleSystem)
                {
                    GUIStyle style = new GUIStyle();
                    var progressRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, style);
                    //EditorGUI.DrawRect(progressRect, Color.red);
                    //particleSystem.main.time = GUI.HorizontalSlider(progressRect, (float)_player.time, 0, _player.GetCurrentClipLength(), style, style);
                    float length = particleSystem.main.duration;
                    EditorGUI.ProgressBar(progressRect, (float)particleSystem.time / length,
                        string.Format("{0} : {1}s", particleSystem.name, length.ToString("0.00")));

                    using (EditorHelper.Horizontal.Do())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Play", "ButtonLeft", GUILayout.Width(50), GUILayout.Height(30)))
                        {
                            Selection.activeGameObject = _mainTarget;
                            foreach (var ps in _targetInfo.particleSystems)
                            {
                                ps.Play();
                            }
                        }

                        if (GUILayout.Button("Restart", "ButtonMid", GUILayout.Width(50), GUILayout.Height(30)))
                        {
                            Selection.activeGameObject = _mainTarget;
                            foreach (var ps in _targetInfo.particleSystems)
                            {
                                ps.Stop();
                                ps.Play();
                            }
                        }

                        if (GUILayout.Button("Stop", "ButtonMid", GUILayout.Width(50), GUILayout.Height(30)))
                        {
                            Selection.activeGameObject = _mainTarget;
                            foreach (var ps in _targetInfo.particleSystems)
                            {
                                ps.Clear();
                            }
                        }

                        if (GUILayout.Button("Pause", "ButtonRight", GUILayout.Width(50), GUILayout.Height(30)))
                        {
                            Selection.activeGameObject = _mainTarget;
                            foreach (var ps in _targetInfo.particleSystems)
                            {
                                ps.Pause();
                            }
                        }

                        GUILayout.FlexibleSpace();
                    }
                }
            }
        }

        void OnGUI_Misc()
        {
            EditorHelper.IconLabel(typeof(GameObject), "Misc");
            EditorHelper.FoldGroup.Do("Manage Data", false, () =>
            {
                //settings.autoLoad = GUILayout.Toggle(settings.autoLoad, "Auto Load Selection", "Button", GUILayout.Height(32));

                using (new EditorGUI.DisabledScope(!EditorPrefs.HasKey(DataManager.key)))
                {
                    using (EditorHelper.Horizontal.Do())
                    {
                        if (GUILayout.Button("Load", EditorStyles.miniButtonLeft))
                        {
                            var path = (EditorUtility.OpenFilePanel("Load Settings File", Application.dataPath,
                                "json"));
                            if (!string.IsNullOrEmpty(path))
                            {
                                var json = File.ReadAllText(path);
                                if (!string.IsNullOrEmpty(json))
                                {
                                    JsonUtility.FromJsonOverwrite(json, dataManager);
                                    DataManager.Save();
                                }
                            }
                        }

                        if (GUILayout.Button("Save", EditorStyles.miniButtonMid))
                        {
                            DataManager.Save();
                        }

                        if (GUILayout.Button("Delete", EditorStyles.miniButtonRight))
                        {
                            DataManager.DeleteAll();
                        }
                    }
                }

                EditorHelper.GridLayout(dataManager.dataList.Count, 2, (i) =>
                {
                    using (EditorHelper.Horizontal.Do())
                    {
                        using (EditorHelper.LabelWidth.Do(20))
                        {
                            var data = dataManager.dataList[i];
                            EditorGUILayout.PrefixLabel(i.ToString());
                            data.name = EditorGUILayout.TextField(data.name);
                        }
                    }
                });
            });

            EditorHelper.FoldGroup.Do("Manage View", false, () =>
            {
                EditorHelper.GridLayout(dataManager.current.viewList.Count, 2, (i) =>
                {
                    using (EditorHelper.Horizontal.Do())
                    {
                        using (EditorHelper.LabelWidth.Do(20))
                        {
                            var view = dataManager.current.viewList[i];
                            EditorGUILayout.PrefixLabel(i.ToString());
                            view.name = EditorGUILayout.TextField(view.name);
                        }
                    }
                });
            });
            EditorHelper.FoldGroup.Do("Manage Lighting", false, () =>
            {
                EditorHelper.GridLayout(dataManager.current.lightingList.Count, 2, (i) =>
                {
                    using (EditorHelper.Horizontal.Do())
                    {
                        using (EditorHelper.LabelWidth.Do(20))
                        {
                            var lighting = dataManager.current.lightingList[i];
                            EditorGUILayout.PrefixLabel(i.ToString());
                            lighting.name = EditorGUILayout.TextField(lighting.name);
                        }
                    }
                });
            });
            EditorHelper.FoldGroup.Do("Resources", false, () =>
            {
                EditorGUILayout.ObjectField(_preview.camera.targetTexture, typeof(RenderTexture), false);
                EditorGUILayout.ObjectField(_wireMaterial, typeof(Material), false);
                EditorGUILayout.ObjectField(_shadowMaterial, typeof(Material), false);
                EditorGUILayout.ObjectField(_heightFogMaterial, typeof(Material), false);
                EditorGUILayout.ObjectField(_depthMaterial, typeof(Material), false);
                EditorGUILayout.ObjectField(_depthNormalMaterial, typeof(Material), false);

            });
            EditorHelper.FoldGroup.Do("Updater", true, () =>
            {
                if (Updater.outOfDate)
                {
                    EditorGUILayout.HelpBox(Updater.updateCheck, MessageType.Error);
                    if (GUILayout.Button("Download latest version", GUILayout.ExpandHeight(true)))
                    {
                        Application.OpenURL(Updater.downloadUrl);
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox(Updater.updateCheck, MessageType.Info);
                }
            });

            EditorHelper.FoldGroup.Do("Shortcuts", true,
                () => { EditorGUILayout.HelpBox(Shortcuts.Print(), MessageType.None); });

            EditorGUILayout.LabelField("Copyright (c) 2020, See1Studios.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.LabelField("see1studios@gmail.com", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.LabelField("Jongwoo Park", EditorStyles.centeredGreyMiniLabel);
        }

        void OnGUI_Info(Rect r)
        {
            if (!_overlayEnabled) return;
            Rect area = new RectOffset(4, 4, 4, 4).Remove(r);
            var style = new GUIStyle(EditorStyles.miniLabel);
            style.alignment = TextAnchor.LowerLeft;
            style.normal.textColor = Color.white;

            _sb0.Append(string.Format("{0} : {1}({2})", "RenderPipeline", currentData.renderPipelineAsset ? currentData.renderPipelineAsset.name : string.Empty, currentData.renderPipelineMode.ToString()));
            _sb0.AppendLine();
            _sb0.Append(string.Format("{0} : {1}", "ColorSpace", PlayerSettings.colorSpace.ToString()));
            _sb0.AppendLine();
            _sb0.Append(string.Format("{0} : {1}x{2}", "Viewport", _viewPortRect.width.ToString("0"), _viewPortRect.height.ToString("0")));
            _sb0.AppendLine();
            _sb0.Append(string.Format("{0} : {1}", "Distance", _dist.ToString("0.00")));
            _sb0.AppendLine();
            _sb0.Append(FPS.GetString());
            //_sb0.Append(string.Format("{0}:{1}", "GetObjectPickerControlID : ", EditorGUIUtility.GetObjectPickerControlID().ToString()));
            //_sb0.AppendLine();
            if (EditorGUIUtility.GetObjectPickerControlID() != 0) //picker 가 없을 때는 0
            {
                if (EditorGUIUtility.GetObjectPickerObject() != null)
                {
                    _sb0.AppendLine();
                    _sb0.Append(string.Format("{0}:{1}", "ObjectPicker : ", EditorGUIUtility.GetObjectPickerObject()));
                    //_sb0.AppendLine();
                }
            }

            //_sb0.Append("\n");
            //_sb0.Append(string.Format("{0}:{1}", "Dest Distance : ", _destDistance.ToString()));
            //_sb0.Append("\n");
            //_sb0.Append(string.Format("{0}:{1}", "Dest Rotation : ", _destRot.ToString()));
            //_sb0.Append("\n");
            //_sb0.Append(string.Format("{0}:{1}", "Dest Pivot Position : ", _destPivotPos.ToString()));
            //_sb0.Append("\n");
            //_sb0.Append(string.Format("{0}:{1}", "Viewport Rect : ", _viewPortRect.ToString()));
            //_sb0.Append("\n");
            //_sb0.Append(string.Format("RenderTexture : {0}:{1}x{2}", _preview.camera.targetTexture.GetInstanceID(), _preview.camera.targetTexture.width, _preview.camera.targetTexture.height.ToString()));
            //_sb0.Append("\n");
            _viewInfo = new GUIContent(_sb0.ToString());
            _sb0.Length = 0;
            var infoSize = style.CalcSize(_viewInfo);
            Rect infoRect = new Rect(area.x, area.y + area.height - infoSize.y, infoSize.x, infoSize.y);
            EditorGUI.DropShadowLabel(infoRect, _viewInfo, style);
        }

        void OnGUI_Log(Rect r)
        {
            if (!_overlayEnabled) return;
            Notice.OnGUI(r);
        }

        void OnGUI_Gizmos(Rect r)
        {
            //Handles.ClearCamera 를 호출해줘야 Preview Camera 가 정상적으로 렌더링 가능 그런데...
            //Handles.ClearCamera 호출시 GUI 를 더 이상 그리지 않으므로 GUI 마지막에서 호출해줘야 다른 GUI 가 잘 나옴
            //Render(updateFOV) 때문에 제 위치에 안나옴.PreviewRenderUtility.Render 에서 참조
            if (_mainTarget && (_gizmoMode != 0))
            {
                if (Event.current.type == EventType.Repaint)
                {
                    Rect gizmoRect = new RectOffset((int)(r.x / currentData.renderScale), 0, 0, 0).Remove(r); //이유 불명. 이렇게 해야 제 위치에 나옴 ㅜㅠ

                    //Rect gizmoRect = (settings.viewportMultiplier > 1) ? r : _rs.center;
                    //EditorGUI.DrawRect(gizmoRect, Color.red * 0.5f);
                    //Store FOV
                    float fieldOfView = _preview.camera.fieldOfView;
                    var rt = _preview.camera.targetTexture;
                    //if (_updateFOV)
                    //    _preview.camera.fieldOfView =
                    //        (float)((double)Mathf.Atan(
                    //            (rt.width > 0 ? Mathf.Max(1f, (float)rt.height / (float)rt.width) : 1f) *
                    //            Mathf.Tan((float)((double)_preview.camera.fieldOfView * 0.5 *
                    //                               (Math.PI / 180.0)))) * 57.2957801818848 * 2.0);
                    //Set Camera
                    Handles.SetCamera(gizmoRect, _preview.camera);
                    DrawWorldAxis();
                    var scale = _targetInfo.bounds.size.magnitude;

                    DrawBasis(_mainTarget.transform, scale * 0.1f, true);

                    DrawBasis(_camPivot.transform, scale * 0.1f, true);
                    Handles.Label(_camPivot.transform.position,
                        string.Format("Pivot: Position {0} Rotation {1}\nCam: Postion {2} Rotation {3}",
                            _camPivot.transform.position, _camPivot.transform.rotation.eulerAngles, _camTr.position,
                            _camTr.rotation.eulerAngles), EditorStyles.miniLabel);

                    var length = 0.05f; // _maxDistance;
                    Handles.color = Color.magenta * 1f;
                    Vector3 rotateCenter = _camPivot.position - _targetOffset;
                    Handles.DrawLine(rotateCenter, rotateCenter + Vector3.right * length);
                    Handles.DrawLine(rotateCenter, rotateCenter - Vector3.right * length);
                    Handles.DrawLine(rotateCenter, rotateCenter + Vector3.up * length);
                    Handles.DrawLine(rotateCenter, rotateCenter - Vector3.up * length);
                    Handles.DrawLine(rotateCenter, rotateCenter + Vector3.forward * length);
                    Handles.DrawLine(rotateCenter, rotateCenter - Vector3.forward * length);
                    Handles.Label(rotateCenter + new Vector3(0, 0.1f, 0),
                        string.Format("View Pivot : {0}\nCam Pivot: {1}\nOffset : {2}", rotateCenter,
                            _camPivot.transform.position, _targetOffset), EditorStyles.miniLabel);

                    //DrawGrid();

                    DrawBasis(_mainTarget.transform, scale * 0.1f, true);
                    if ((_gizmoMode & GizmoMode.Info) == GizmoMode.Info)
                    {
                        Handles.color = Color.white;
                        DrawBasis(_mainTarget.transform, scale * 0.1f, true);
                        Handles.Label(_mainTarget.transform.position, _targetInfo.Print(), EditorStyles.miniLabel);
                    }

                    if ((_gizmoMode & GizmoMode.Bound) == GizmoMode.Bound)
                    {
                        Handles.color = Color.white * 0.5f;
                        float size = 4.0f;
                        Handles.DrawWireCube(_targetInfo.bounds.center, _targetInfo.bounds.size);
                        Handles.DrawDottedLine(_targetInfo.bounds.center,
                            _targetInfo.bounds.center + Vector3.right * _targetInfo.bounds.extents.x, size);
                        Handles.DrawDottedLine(_targetInfo.bounds.center,
                            _targetInfo.bounds.center + Vector3.right * -_targetInfo.bounds.extents.x, size);
                        Handles.DrawDottedLine(_targetInfo.bounds.center,
                            _targetInfo.bounds.center + Vector3.up * _targetInfo.bounds.extents.y, size);
                        Handles.DrawDottedLine(_targetInfo.bounds.center,
                            _targetInfo.bounds.center + Vector3.up * -_targetInfo.bounds.extents.y, size);
                        Handles.DrawDottedLine(_targetInfo.bounds.center,
                            _targetInfo.bounds.center + Vector3.forward * _targetInfo.bounds.extents.z, size);
                        Handles.DrawDottedLine(_targetInfo.bounds.center,
                            _targetInfo.bounds.center + Vector3.forward * -_targetInfo.bounds.extents.z, size);
                        string boundInfo = string.Format(
                            "Bounds\nCenter : {0}\nExtents : {1}\nSize : {2}\nMin : {3}\nMax : {4}\n",
                            _targetInfo.bounds.center.ToString(), _targetInfo.bounds.extents.ToString(),
                            _targetInfo.bounds.size.ToString(), _targetInfo.bounds.min.ToString(),
                            _targetInfo.bounds.max.ToString());
                        Handles.Label(_targetInfo.bounds.max, boundInfo, EditorStyles.miniLabel);
                        foreach (var ps in _targetInfo.particleSystems)
                        {
                            ShowBounds(ps);
                        }
                    }

                    if ((_gizmoMode & GizmoMode.Bone) == GizmoMode.Bone)
                    {
                        CompareFunction zTest = Handles.zTest;
                        Handles.zTest = CompareFunction.LessEqual;
                        //Draw Bones
                        foreach (var bone in _targetInfo.bones)
                        {
                            if (bone == null) continue;
                            if (bone.parent == null) continue;
                            Handles.color = Color.yellow;
                            //var endpoint = bone.parent.position + bone.parent.rotation * bone.localPosition;
                            Handles.DrawLine(bone.position, bone.parent.position);
                            Handles.color = Color.cyan;
                            Handles.SphereHandleCap(0, bone.position, bone.rotation, 0.01f, EventType.Repaint);
                            DrawBasis(bone, scale * 0.02f, false);
                            //var midPoint = (bone.position + bone.parent.position) / 2;
                            var parentDirection = bone.position + (bone.position - bone.parent.position) * 0.1f;
                            var d = Mathf.Clamp01(1 / _destDistance);
                            GUI.color = Color.Lerp(Color.clear, Color.white, d);
                            if (d > 0.5f) Handles.Label(parentDirection, bone.name, EditorStyles.miniLabel);
                            GUI.color = Color.white;
                        }

                        Handles.zTest = zTest;
                    }

                    if ((_gizmoMode & GizmoMode.Light) == GizmoMode.Light)
                    {
                        for (var i = 0; i < _preview.lights.Length; i++)
                        {
                            var previewLight = _preview.lights[i];
                            if (previewLight.gameObject.activeSelf)
                            {
                                var color = previewLight.color;
                                var tr = previewLight.transform;
                                tr.position = Vector3.zero;
                                Handles.color = new Color(color.r, color.g, color.b, 1f);
                                Handles.CircleHandleCap(0, tr.position + tr.forward * -scale,
                                    tr.rotation * Quaternion.LookRotation(Vector3.forward), scale * 0.5f,
                                    EventType.Repaint);
                                Handles.DrawLine(tr.position + tr.forward * -scale, tr.position);
                                Handles.color = new Color(color.r, color.g, color.b, 0.1f);
                                Handles.DrawSolidDisc(tr.position + tr.forward * -scale, tr.forward, scale * 0.5f);
                                Handles.DrawSolidDisc(tr.position + tr.forward * -scale, tr.forward,
                                    scale * 0.5f * previewLight.intensity * 0.5f);
                                string lightInfo = string.Format("Light {0}\nColor : {1}\nRotation : {2}\n",
                                    i.ToString(), color.ToString(), tr.rotation.eulerAngles.ToString());
                                Handles.Label(tr.position + tr.forward * -scale, lightInfo, EditorStyles.miniLabel);
                            }
                        }
                    }

                    Handles.ClearCamera(gizmoRect, _preview.camera);
                    //Restore FOV
                    _preview.camera.fieldOfView = fieldOfView;
                    GUIUtility.ExitGUI();
                }
            }
        }
#endregion

        #region Gizmos
        void DrawWorldAxis()
        {
            Color color = Handles.color;
            Handles.color = Handles.xAxisColor;
            Handles.DrawLine(Vector3.zero, Vector3.right);
            Handles.color = Handles.yAxisColor;
            Handles.DrawLine(Vector3.zero, Vector3.up);
            Handles.color = Handles.zAxisColor;
            Handles.DrawLine(Vector3.zero, Vector3.forward);
            Handles.color = color;
        }

        void DrawBasis(Transform transform, float size, bool arrow)
        {
            Color color = Handles.color;
            Handles.color = Handles.xAxisColor;
            if (arrow)
                Handles.ArrowHandleCap(0, transform.position,
                    transform.rotation * Quaternion.LookRotation(Vector3.right), size, EventType.Repaint);
            else
                Handles.DrawLine(transform.position, transform.position + transform.right * size);

            Handles.color = Handles.yAxisColor;
            if (arrow)
                Handles.ArrowHandleCap(0, transform.position, transform.rotation * Quaternion.LookRotation(Vector3.up),
                    size, EventType.Repaint);
            else
                Handles.DrawLine(transform.position, transform.position + transform.up * size);
            Handles.color = Handles.zAxisColor;
            if (arrow)
                Handles.ArrowHandleCap(0, transform.position,
                    transform.rotation * Quaternion.LookRotation(Vector3.forward), size, EventType.Repaint);
            else
                Handles.DrawLine(transform.position, transform.position + transform.forward * size);
            Handles.color = color;
        }

        void DrawGrid()
        {
            Handles.zTest = CompareFunction.Greater;
            Color color = Handles.color;
            Handles.color = Color.gray * 0.5f;
            int count = 9;
            int d = count * 2;
            Vector3 offset = new Vector3(-count, 0, -count);
            Vector3 startPos = Vector3.zero + offset;
            for (int i = 0; i < d + 1; i++)
            {
                Vector3 pos = startPos + new Vector3(i, 0, 0);
                Handles.DrawLine(pos, pos + Vector3.forward * d);
            }

            for (int j = 0; j < d + 1; j++)
            {
                Vector3 pos = startPos + new Vector3(0, 0, j);
                Handles.DrawLine(pos, pos + Vector3.right * d);
            }

            Handles.color = color;
        }

        #endregion

        #region Input

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
            if (isLDoubleClicked) FitTargetToViewport();
            if (isRDoubleClicked) ResetLight();
            axis0 *= currentData.rotSpeed;
            axis2 *= currentData.panSpeed;
            zoom *= currentData.zoomSpeed;
            UpdateCamera(axis0, axis2, zoom);
            UpdateLight(axis1);
            //Keybord Shortcut
            if (_shortcutEnabled && evt.isKey && evt.type == EventType.KeyDown && !EditorGUIUtility.editingTextField)
            {
                Shortcuts.ProcessInput(evt.keyCode);
                GUIUtility.ExitGUI();
            }
        }
        void FitTargetToViewport()
        {
            if (_mainTarget)
            {
                CalcMinMaxDistance();
                _destPivotPos = _targetInfo.bounds.center;
                _destDistance = GetFitDistanceOfCamera(_targetInfo.bounds, _preview.camera);
            }
        }

        void CalcMinMaxDistance()
        {
            if (_mainTarget)
            {
                Vector3 size = _targetInfo.bounds.max - _targetInfo.bounds.min;
                float largestSize = Mathf.Max(size.x, size.y, size.z);
                float distance = GetFitDistanceOfCamera(_targetInfo.bounds, _preview.camera);
                _minDistance = distance * 0.01f;
                _maxDistance = largestSize * 100f;
                SetClipPlane();
            }
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
            _preview.camera.nearClipPlane = _dist * 0.1f;
            _preview.camera.farClipPlane = _maxDistance * 2;
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
            _preview.cameraFieldOfView = Mathf.Lerp(_preview.cameraFieldOfView, _destFOV, _deltaTime * smoothFactor);

            //Final
            _camTr.position = pivotPos - (rotation * Vector3.forward * _dist + _targetOffset);
            SetClipPlane();

            //Ortho
            if (_preview.camera.orthographic)
            {
                _preview.camera.orthographicSize = _destDistance * _preview.cameraFieldOfView * 0.01f;
            }
        }

        void UpdateLight(Vector2 axis)
        {
            var angle = new Vector3(axis.y, -axis.x, 0) * currentData.rotSpeed;
            for (int i = 0; i < _lightRotationIndex + 1; i++)
            {
                var lightTr = _preview.lights[i].transform;
                lightTr.Rotate(angle, Space.World);
            }
        }

        void ResetLight()
        {
            _preview.lights[0].transform.rotation = Quaternion.identity;
            _preview.lights[0].color = new Color(0.769f, 0.769f, 0.769f, 1.0f);
            _preview.lights[0].intensity = 1;
            _preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
            _preview.lights[1].color = new Color(0.28f, 0.28f, 0.315f, 1.0f);
            _preview.lights[1].intensity = 1;

            var angle = new Vector3(0, -180, 0);

            for (int i = 0; i < _preview.lights.Length; i++)
            {
                _preview.lights[i].cullingMask = ~_previewLayer;
                var lightTr = _preview.lights[i].transform;
                lightTr.Rotate(angle);

                _preview.lights[i].shadows =
                    currentData.shadowEnabled && i == 0 ? LightShadows.Soft : LightShadows.None;
                _preview.lights[i].shadowResolution = LightShadowResolution.VeryHigh;
                _preview.lights[i].shadowBias = 0.01f;
            }

            _preview.ambientColor = currentData.ambientSkyColor = Color.gray;
        }

        static ParticleSystem GetRoot(ParticleSystem ps)
        {
            if ((Object)ps == (Object)null)
                return (ParticleSystem)null;
            Transform transform = ps.transform;
            while ((bool)(Object)transform.parent &&
                   (Object)transform.parent.gameObject.GetComponent<ParticleSystem>() != (Object)null)
                transform = transform.parent;
            return transform.gameObject.GetComponent<ParticleSystem>();
        }

        void ShowBounds(ParticleSystem ps)
        {
            if (ps.particleCount > 0)
            {
                ParticleSystemRenderer component = ps.GetComponent<ParticleSystemRenderer>();
                Color color = Handles.color;
                Handles.color = Color.yellow;
                Bounds bounds = component.bounds;
                Handles.DrawWireCube(bounds.center, bounds.size);
                Handles.color = color;
            }

            foreach (ParticleSystem componentsInChild in ps.transform.GetComponentsInChildren<ParticleSystem>())
            {
                ParticleSystem child = componentsInChild;
                if ((UnityEngine.Object)child != (UnityEngine.Object)ps &&
                    !((UnityEngine.Object)((IEnumerable<ParticleSystem>)_targetInfo.particleSystems)
                      .FirstOrDefault<ParticleSystem>((Func<ParticleSystem, bool>)(o =>
                          (UnityEngine.Object)GetRoot(o) == (UnityEngine.Object)child)) !=
                      (UnityEngine.Object)null))
                    this.ShowBounds(child);
            }
        }

        void ApplyView(int viewListIndex)
        {
            if (currentData.viewList.Count - 1 < viewListIndex) return;
            var view = currentData.viewList[viewListIndex];
            var message = string.Format("View {0} Loaded", viewListIndex.ToString());
            ApplyView(view, message);
        }

        void ApplyView(View view, string message = "")
        {
            _destRot = view.rotation;
            _destDistance = view.distance;
            _destPivotPos = view.pivot;
            _destFOV = view.fieldOfView;
            CalcMinMaxDistance();
            Notice.Log(message, false);
        }

        void ApplyReflectionEnvironment()
        {
            _probe.customBakedTexture = currentData.cubeMap;
            currentData.CubeMapMipMapBias = currentData.CubeMapMipMapBias;
            DynamicGI.UpdateEnvironment();
        }

        private void ApplyBackground()
        {
            if (currentData.clearFlag == ClearFlags.Sky)
            {
                _preview.camera.clearFlags = CameraClearFlags.Skybox;
            }
            else
            {
                _preview.camera.backgroundColor = currentData.bgColor;
                _preview.camera.clearFlags = CameraClearFlags.SolidColor;
            }
        }

        private void ApplyEnvironment()
        {
            
        }

        void ApplyLighting(Lighting lighting, string message = "")
        {
            for (int i = 0; i < _preview.lights.Length; i++)
            {
                try
                {
                    var light = lighting.lightList[i];
                    _preview.lights[i].color = light.lightColor;
                    _preview.lights[i].intensity = light.intensity;
                    _preview.lights[i].transform.position = light.position;
                    _preview.lights[i].transform.rotation = light.rotation;

                }
                catch
                {
                    _preview.lights[i].color = Color.white;
                    _preview.lights[i].intensity = 1;
                }
            }

            dataManager.current.ambientSkyColor = lighting.ambientSkyColor;
            Notice.Log(message, false);
        }

        IEnumerator Interpolate(float value, float startValue, float endValue, float time)
        {
            float elapedTime = 0f;
            while (elapedTime < time)
            {
                elapedTime += _deltaTime;
                var delta = elapedTime / time;
                value = Mathf.Lerp(startValue, endValue, delta);
                yield return value;
            }
        }

        #endregion

        #region Utils

        public static List<GameObject> FindAllObjectsInScene()
        {
            UnityEngine.SceneManagement.Scene activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();

            GameObject[] rootObjects = activeScene.GetRootGameObjects();

            GameObject[] allObjects = Resources.FindObjectsOfTypeAll<GameObject>();

            List<GameObject> objectsInScene = new List<GameObject>();

            for (int i = 0; i < rootObjects.Length; i++)
            {
                objectsInScene.Add(rootObjects[i]);
            }

            for (int i = 0; i < allObjects.Length; i++)
            {
                if (allObjects[i].transform.root)
                {
                    for (int i2 = 0; i2 < rootObjects.Length; i2++)
                    {
                        if (allObjects[i].transform.root == rootObjects[i2].transform &&
                            allObjects[i] != rootObjects[i2])
                        {
                            objectsInScene.Add(allObjects[i]);
                            break;
                        }
                    }
                }
            }

            return objectsInScene;
        }

        public static Mesh GetMesh(GameObject gameObject)
        {
            MeshFilter mf = gameObject.GetComponent<MeshFilter>();

            if (mf != null && mf.sharedMesh != null)
                return mf.sharedMesh;

            SkinnedMeshRenderer smr = gameObject.GetComponent<SkinnedMeshRenderer>();

            if (smr != null && smr.sharedMesh != null)
                return smr.sharedMesh;
            else
                return null;
        }

        Shader FindShader(string shaderName)
        {
            Shader shader = Shader.Find(shaderName);
            if (!shader)
            {
                string fallBackName = currentData.renderPipelineMode == RenderPipelineMode.Universal ? "Universal Render Pipeline/Unlit" : "Unlit/Color";
                shader = Shader.Find(fallBackName);
                Notice.Log(string.Format("{0} Shader not found. Fallback to {1}", shaderName, fallBackName));
            }

            return shader;
        }

        static Color GetGrayscaleInverted(Color input)
        {
            Vector3 color = new Vector3(input.r, input.g, input.b);
            Vector3 factor = new Vector3(0.2125f, 0.7154f, 0.0721f);
            float luminance = Vector3.Dot(color, factor);
            float output = 1 - luminance;
            return new Color(output, output, output, 1);
        }

        static float ClampAngle(float angle, float min, float max)
        {
            if (angle < -360F)
                angle += 360F;
            if (angle > 360F)
                angle -= 360F;
            return Mathf.Clamp(angle, min, max);
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
            Notice.Log("Image saved to: " + imageFilePath, true);
            return imageFilePath;
        }

        static string SaveAsPSD(Texture2D texture, string folder, string name, string dateTimeFormat)
        {
            //string timeString = DateTime.Now.ToString(dateTimeFormat);
            //var imageFilePath = folder + "/" + MakeValidFileName(string.Format("{0}_{1}.{2}", name, timeString, "psd"));
            //var directoryInfo = (new FileInfo(imageFilePath)).Directory;
            //if (directoryInfo != null) directoryInfo.Create();

            //byte[] bytes = texture.GetRawTextureData();//.EncodeToPNG();
            //FileStream fs = new FileStream(imageFilePath, FileMode.OpenOrCreate);
            ////PsdBinaryWriter writer = new PsdBinaryWriter(fs);
            ////writer.Write(bytes);
            ////writer.Write(bytes);
            //PsdFile file =new PsdFile();
            //file.PrepareSave();
            //file.Save(fs);
            //Notice.Log("Image saved to: " + imageFilePath, true);
            //return imageFilePath;
            return "";
        }

        static string MakeValidFileName(string name)
        {
            var invalidChars = System.Text.RegularExpressions.Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            var invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);

            return System.Text.RegularExpressions.Regex.Replace(name, invalidRegStr, "_");
        }

        private void ResizeWindow(Vector2 viewPortSize)
        {
            var pos = new Vector2(position.position.x, position.position.y + 5);
            var size = new Vector2(viewPortSize.x + _rs.left.size.x + _rs.right.size.x,
                viewPortSize.y + _rs.top.size.y + _rs.bottom.size.y);
#if UNITY_2018
             pos += new Vector2(0, 22);
            size -= new Vector2(0, 3); //bug maybe
#endif
            if (IsDocked())
            {
            }
            else
            {
                this.position = new Rect(pos, size);
            }
        }

        static bool IsScrollBarExists(Rect rect)
        {
            // SCROLL BARS VISIBILITY DETECTION PHASE 1
            float scrollBar__y = 0f;
            // To catch whether scrollbars are visible we get the y value now and the height value later
            if (Event.current.type == EventType.Repaint)
            {
                scrollBar__y = GUILayoutUtility.GetLastRect().y;
            }

            // SCROLL BARS VISIBILITY DETECTION PHASE 2
            float scrollBar__height = 0f;
            // We now get the height and then
            if (Event.current.type == EventType.Repaint)
            {
                scrollBar__height = GUILayoutUtility.GetLastRect().height;
            } // SCROLL BARS VISIBILITY DETECTION PHASE 3

            // Determine whether scrollbars are visible
            if (Event.current.type == EventType.Repaint)
            {
                return (scrollBar__y > scrollBar__height);
            }

            return false;
        }

        bool ScrollBarDetector(EditorGUILayout.ScrollViewScope scope)
        {
            Vector2 detectionValue = new Vector2(0f, 0.1f);
            // Set the Inspector's Scrollbar position value to an arbitrary, yet extremely unlikely value.
            scope.GetType().GetProperty("scrollPosition").SetValue(scope, detectionValue, null);
            // ScrollBar Detection
            //Vector2 scrollValue = (Vector2)scope.GetType().GetProperty("scrollPosition").GetValue(scope, null);
            // During the Repaint Event, it is finally calculated whether a scrollbar is needed, and the inspector's scroll position value is updated.
            // If a scrollbar is not needed, the value will be reset to 0, thus allowing detection of the scrollbar that will be accurate 99.9% of the time.
            if (Event.current.type == EventType.Repaint)
            {

                // If the scroll position does not match the detection value, 
                //       it is ~99% likely that no scroll bar exists
                return (_scrollPos != detectionValue);
            }

            return false;
        }

        static void ShowHideAll(GameObject parent, bool enabled)
        {
            if (parent)
            {
                var renderers = parent.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    renderers[i].enabled = enabled;
                }
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

        private void DrawMesh(Mesh mesh, Material material, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            //if (material == null || mesh == null) return;
            //Graphics.SetRenderTarget(_preview.camera.targetTexture);
            //material.SetPass(0);
            Graphics.DrawMesh(mesh, Matrix4x4.TRS(position, rotation, scale),material, _previewLayer, _preview.camera, 0, null, false, true ); 
        }


        private static void ShowMenu<T>(T selected, string[] itemNames, T[] items, GenericMenu.MenuFunction2 OnSelected)
        {
            // create the menu and add items to it
            GenericMenu menu = new GenericMenu();
            for (int i = 0; i < itemNames.Length; i++)
            {
                menu.AddItem(new GUIContent(itemNames[i]), selected.Equals(items[i]), OnSelected, items[i]);
            }
            menu.ShowAsContext();
        }
        
        //모든 트랜스폼의 path를 생성해서 리스트로 뽑음
        private static string[] BuildHierachialPath(Transform root)
        {
            Transform[] all = root.GetComponentsInChildren<Transform>();
            List<string> list = new List<string>();
            foreach (var tr in all)
            {
                list.Add(GetTransformPath(tr));
            }
            return list.ToArray();
        }

        private static string GetTransformPath(GameObject go)
        {
            string path = string.Join("/", go.GetComponentsInParent<Transform>().Select(t => t.name).Reverse().ToArray());
            //AS_Debug.Log(path);
            return path;
        }

        private static string GetTransformPath(Transform tr)
        {
            Transform root = tr;
            while (root.parent != null)
            {
                root = root.parent;
            }
            root = root.parent;
            return AnimationUtility.CalculateTransformPath(tr, root); //지정한 루트로부터의 path를 생성해줌
        }

        #endregion

        #region Reflection

        private Scene GetPreviewScene()
        {
            var fi = _preview.GetType().GetField("m_PreviewScene", BindingFlags.Instance | BindingFlags.NonPublic);
            if (fi != null)
            {
                var previewScene = fi.GetValue(_preview);
                var scene = (UnityEngine.SceneManagement.Scene)(previewScene.GetType()
                    .GetField("m_Scene", BindingFlags.Instance | BindingFlags.NonPublic)).GetValue(previewScene);
                return scene;
            }

            return EditorSceneManager.NewPreviewScene();
        }

        private void InitPreviewLayerID()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var propInfo = typeof(Camera).GetProperty("PreviewCullingLayer", flags);
            _previewLayer = (int)propInfo.GetValue(null, new object[0]);
            Notice.Log(string.Format("{0} : PreviewLayerID is {1}", this.GetType().Name, _previewLayer.ToString()));
        }

        bool IsDocked()
        {
            BindingFlags fullBinding = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                       BindingFlags.Static;
            MethodInfo isDockedMethod = typeof(EditorWindow).GetProperty("docked", fullBinding).GetGetMethod(true);
            return (bool)isDockedMethod.Invoke(this, null);
        }

        Shader FindBuiltinShader(string shaderName)
        {
            Type shaderType = typeof(Shader);
            MethodInfo mi = shaderType.GetMethod("FindBuiltin",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Shader shader = (mi != null) ? mi.Invoke(this, new object[] { shaderName }) as Shader : null;
            return shader;
        }

        #endregion

        [MenuItem(Initializer.MENU_PATH, false, Initializer.MENU_PRIORITY)]
        private static void Init()
        {
            See1View window = EditorWindow.GetWindow<See1View>(GUIContents.title.text);
            window.titleContent = GUIContents.title;
            window.minSize = new Vector2(128, 128);
            window.Show();
        }
    }
}