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
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
#if UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
#endif

namespace See1.Editor
{
    public class See1View : EditorWindow
    {
        #region Inner Classes

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
                            advance = data.current == enumerator.Current; //a IEnumerator or a plain object was passed to the implementation
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
                    Debug.LogAssertion("EditorCoroutine handle is null.");
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

        public class Shaders
        {
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
            private static Shader _depthNormal;
            public static Shader depthNormal
            {
                get
                {
                    if (_depthNormal == null)
                    {
                        _depthNormal = ShaderUtil.CreateShaderAsset(
                            "Shader \"See1View/DepthNormal\"\n{\nProperties\n{\n_MainTex (\"Texture\", 2D) = \"white\" {}\n}\nSubShader\n{\n// No culling or depth\nCull Off ZWrite Off ZTest Always\n\nPass\n{\nCGPROGRAM\n#pragma vertex vert\n#pragma fragment frag\n\n#include \"UnityCG.cginc\"\n\nsampler2D _MainTex;\nsampler2D _CameraDepthNormalsTexture;\nfloat4 _CameraDepthNormalsTexture_TexelSize;\n\nstruct appdata\n{\nfloat4 vertex : POSITION;\nfloat2 uv : TEXCOORD0;\n};\n\nstruct v2f\n{\nfloat2 uv : TEXCOORD0;\nfloat4 vertex : SV_POSITION;\n};\n\nv2f vert (appdata v)\n{\nv2f o;\no.vertex = UnityObjectToClipPos(v.vertex);\no.uv = v.uv;\nreturn o;\n}\n\nfixed4 frag (v2f i) : SV_Target\n{\nfixed3 tex = tex2D(_MainTex, i.uv).rgb;\nfixed4 col = tex2D(_CameraDepthNormalsTexture, i.uv);\nfloat depth;\nfloat3 normal;\nDecodeDepthNormal(col, depth, normal);\n//fixed grayscale = Luminance(tex.rgb);\n//return float4(grayscale,grayscale,grayscale, 1);\nreturn float4(normal, 1);\n}\nENDCG\n}\n}\n}");
                    }
                    return _depthNormal;
                }
            }
        }

        [Flags]
        public enum GizmoMode
        {
            //None = (1<<0),
            Info = (1 << 1),
            Light = (1 << 2),
            Bound = (1 << 3),
            Bone = (1 << 4)
        }

        public enum SidePanelMode
        {
            View,
            Model,
            Animation,
            Tools
        }

        public enum ClearFlags
        {
            Color,
            Sky
        }

        public enum FileExistsMode
        {
            Overwrite,
            Rename
        }

        [Serializable]
        class SmoothAnimBool : BaseAnimValue<bool>
        {
            [SerializeField] private float m_Value;

            public SmoothAnimBool()
                : base(false)
            {
            }

            public SmoothAnimBool(bool value)
                : base(value)
            {
            }

            public SmoothAnimBool(UnityAction callback)
                : base(false, callback)
            {
            }

            public SmoothAnimBool(bool value, UnityAction callback)
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
            public SmoothAnimBool openTop;
            public SmoothAnimBool openLeft;
            public SmoothAnimBool openRight;
            public SmoothAnimBool openBottom;
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
                this.openTop = new SmoothAnimBool(false);
                this.openBottom = new SmoothAnimBool(false);
                this.openLeft = new SmoothAnimBool(false);
                this.openRight = new SmoothAnimBool(false);
            }


            public RectSlicer(EditorWindow window)
            {
                this.window = window;
                UnityAction onChangeCallback = window.Repaint;
                this.openTop = new SmoothAnimBool(false);
                this.openTop.valueChanged.AddListener(onChangeCallback);
                this.openBottom = new SmoothAnimBool(false);
                this.openBottom.valueChanged.AddListener(onChangeCallback);
                this.openLeft = new SmoothAnimBool(false);
                this.openLeft.valueChanged.AddListener(onChangeCallback);
                this.openRight = new SmoothAnimBool(false);
                this.openRight.valueChanged.AddListener(onChangeCallback);
            }

            public RectSlicer(UnityAction onChangeCallback)
            {
                this.openTop = new SmoothAnimBool(false);
                this.openTop.valueChanged.AddListener(onChangeCallback);
                this.openBottom = new SmoothAnimBool(false);
                this.openBottom.valueChanged.AddListener(onChangeCallback);
                this.openLeft = new SmoothAnimBool(false);
                this.openLeft.valueChanged.AddListener(onChangeCallback);
                this.openRight = new SmoothAnimBool(false);
                this.openRight.valueChanged.AddListener(onChangeCallback);
            }

            public RectSlicer(Rect r, UnityAction onChangeCallback)
            {
                this.rect = r;
                this.openTop = new SmoothAnimBool(false);
                this.openTop.valueChanged.AddListener(onChangeCallback);
                this.openBottom = new SmoothAnimBool(false);
                this.openBottom.valueChanged.AddListener(onChangeCallback);
                this.openLeft = new SmoothAnimBool(false);
                this.openLeft.valueChanged.AddListener(onChangeCallback);
                this.openRight = new SmoothAnimBool(false);
                this.openRight.valueChanged.AddListener(onChangeCallback);
            }

            public RectSlicer(Rect r, float topHeight, float bottomHeight, float leftWidth, float rightWidth,
                UnityAction onChangeCallback)
            {
                this.rect = r;
                this.openTop = new SmoothAnimBool(false);
                this.openTop.valueChanged.AddListener(onChangeCallback);
                this.openBottom = new SmoothAnimBool(false);
                this.openBottom.valueChanged.AddListener(onChangeCallback);
                this.openLeft = new SmoothAnimBool(false);
                this.openLeft.valueChanged.AddListener(onChangeCallback);
                this.openRight = new SmoothAnimBool(false);
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
                this.openTop = new SmoothAnimBool(openTop);
                this.openTop.valueChanged.AddListener(onChangeCallback);
                this.openBottom = new SmoothAnimBool(openBottom);
                this.openBottom.valueChanged.AddListener(onChangeCallback);
                this.openLeft = new SmoothAnimBool(openLeft);
                this.openLeft.valueChanged.AddListener(onChangeCallback);
                this.openRight = new SmoothAnimBool(openRight);
                this.openRight.valueChanged.AddListener(onChangeCallback);

                this.topTargetHeight = topHeight;
                this.bottomTargetHeight = bottomHeight;
                this.leftTargetWidth = leftWidth;
                this.rightTargetWidth = rightWidth;
            }
        }

        [Serializable]
        internal class Data : ICloneable
        {
            public string name;
            //private static bool _willBeDeleted = false;
            public Color wireLineColor = Color.white;
            public Color wireFillColor = Color.black;
            public float wireThickness = 0.1f;
            public float wireUseDiscard = 1;
            public bool reframeToTarget = true;
            public bool recalculateBound;
            public int rotSpeed = 3;
            public int zoomSpeed = 3;
            public int panSpeed = 3;
            public int smoothFactor = 3;
            public FileExistsMode fileExistsMode = FileExistsMode.Overwrite;
            public bool openSavedImage = true;
            public bool screenShotAlpha = true;
            public int captureMultiplier = 1;
            public bool autoLoad = false;
            public int viewportMultiplier = 2;
            public Color planeShadowColor = Color.gray;
            public bool enablePlaneShadows = true;
            public bool enableShadows = true;
            public float shadowBias = 0.01f;
            public bool enableSRP = false;
            public bool enablePostProcess = true;
            public string lastTargetPath = string.Empty;
            //public string skyMatPath = string.Empty;
            public string cubemapPath = string.Empty;
            public string profilePath = string.Empty;
            public Color bgColor = new Color(0.3215686f, 0.3215686f, 0.3215686f, 1f);
            public Color ambientSkyColor = Color.gray;
            public ClearFlags clearFlag = ClearFlags.Color;
            public View lastView;
            public List<View> viewList = new List<View>();
            public List<Vector2> viewportSizes = new List<Vector2>();
            public List<ModelGroup> modelGroupList = new List<ModelGroup>();
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
#if UNITY_POST_PROCESSING_STACK_V2
            private PostProcessProfile _profile;

            public PostProcessProfile profile
            {
                get
                {
                    return _profile
                        ? _profile
                        : _profile = AssetDatabase.LoadAssetAtPath<PostProcessProfile>(profilePath);
                }
                set
                {
                    _profile = value;
                    profilePath = AssetDatabase.GetAssetPath(value);
                }
            }
#endif
            public Data(string name)
            {
                this.name = name;
            }

            public object Clone()
            {
                return this.MemberwiseClone();
            }
        }

        [Serializable]
        class See1ViewSettings
        {
            private static See1ViewSettings _instance;
            public static See1ViewSettings instance
            {
                get { return (_instance != null) ? _instance : Load(); }
                set { _instance = value; }
            }

            public List<Data> dataList = new List<Data>();
            public static TextAsset dataAsset;
            public Data current
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
            public static string path = "Assets/Editor/See1ViewSettings.json";
            public static readonly string key = string.Format("{0}.{1}", "com.see1.See1View.settings",
                GetProjectName().ToLower());
            public static bool _isDirty;

            public bool Add(string name)
            {
                bool canAdd = CheckName(name);
                while (!canAdd)
                {
                    name += "_1";
                    canAdd = CheckName(name);
                }
                Data data = new Data(name);
                dataList.Add(data);
                dataIndex = dataList.Count-1;
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

            public bool Remove(Data data)
            {
                if (dataList.Contains(data))
                {
                    dataList.Remove(data);
                    Mathf.Clamp(dataIndex -= 1, 0, dataList.Count);
                    return true;
                }
                return false;
            }

            private static See1ViewSettings Load()
            {
                _instance = new See1ViewSettings();
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
                var json = JsonUtility.ToJson(instance,true);
                DirectoryInfo di = new DirectoryInfo(Application.dataPath.Replace("Assets","") + Path.GetDirectoryName(path));
                if(!di.Exists) di.Create();
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
                    if (EditorUtility.DisplayDialog("Removing " + key + "?",
                        "Are you sure you want to " +
                        "delete the editor key " +
                        key + "?, This action cant be undone",
                        "Yes",
                        "No"))
                        EditorPrefs.DeleteKey(key);
                }
                else
                {
                    EditorUtility.DisplayDialog("Could not find " + key,
                        "Seems that " + key +
                        " does not exists or it has been deleted already, " +
                        "check that you have typed correctly the name of the key.",
                        "Ok");
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
                Data data = current.Clone() as Data;
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
        }

        [Serializable]
        internal class ModelGroup
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

        [Serializable]
        internal class View
        {
            public string name;
            public Vector2 rotation;
            public float distance;
            public Vector3 pivot;
            public float fieldOfView;

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
                this.distance = Vector3.Distance(camera.transform.position,Vector3.zero);
                this.pivot = camera.transform.position;
                this.fieldOfView = camera.fieldOfView;
            }
        }

        class TargetInfo
        {
            public string assetPath;
            private StringBuilder sb = new StringBuilder();
            public Bounds bounds;
            public List<Renderer> renderers = new List<Renderer>();
            public List<Transform> bones = new List<Transform>();
            public List<Material> materials = new List<Material>();
            public Animator[] animators;
            public MeshRenderer[] meshRenderers;
            public SkinnedMeshRenderer[] skinnedMeshRenderers;
            public ParticleSystem[] particleSystems;
            public ParticleSystemRenderer[] particleSystemRenderers;
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
            }

            public void Init(GameObject root)
            {
                Cleanup();
                var srcPrefab = PrefabUtility.GetPrefabParent(root);
                assetPath = srcPrefab ? AssetDatabase.GetAssetPath(srcPrefab) : string.Empty;
                sb.Append(root.name);
                sb.Append("\n");
                animators = root.GetComponentsInChildren<Animator>();
                renderers = root.GetComponentsInChildren<Renderer>().ToList();
                meshRenderers = root.GetComponentsInChildren<MeshRenderer>();
                skinnedMeshRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>();
                particleSystems = root.GetComponentsInChildren<ParticleSystem>();
                particleSystemRenderers = root.GetComponentsInChildren<ParticleSystemRenderer>();

                foreach (var renderer in renderers)
                {
                    materials.AddRange(renderer.sharedMaterials);
                    bounds.Encapsulate(renderer.bounds);
                }

                materials = materials.Where(x => x != null).Distinct().ToList();

                if (animators.Length > 0)
                {
                    sb.Append(string.Format("Animators : {0}\n", animators.Count().ToString()));
                }

                if (meshRenderers.Length > 0)
                {
                    sb.Append(string.Format("MeshRenderer : {0}\n", meshRenderers.Length.ToString()));
                }

                if (skinnedMeshRenderers.Length > 0)
                {
                    bones.AddRange(skinnedMeshRenderers.SelectMany(x => x.bones).Distinct());
                    sb.Append(string.Format("SkinnedMeshRenderer : {0}\n", skinnedMeshRenderers.Length.ToString()));
                    sb.Append(string.Format("Bones : {0}\n",
                        skinnedMeshRenderers.SelectMany(x => x.bones).Distinct().Count().ToString()));
                }

                if (particleSystems.Length > 0)
                {
                    foreach (var ps in particleSystems)
                    {
                        ParticleSystemRenderer component = ps.GetComponent<ParticleSystemRenderer>();
                        ps.Simulate(1, true, true, false);
                        bounds.Encapsulate(component.bounds);
                        ps.Clear();
                        ps.Stop();
                    }

                    sb.Append(string.Format("ParticleSystem : {0}\n", particleSystems.Length.ToString()));
                    if (particleSystemRenderers.Length > 0)
                    {
                        sb.Append(string.Format("ParticleSystemRenderer : {0}\n",
                            particleSystemRenderers.Length.ToString()));
                    }
                }

                sb.Append(string.Format("Materials : {0}\n",
                    renderers.SelectMany(x => x.sharedMaterials).Distinct().Count().ToString()));
            }

            public string GetInfoString()
            {
                //Type type = typeof(InternalMeshUtil);
                //Mesh target = this.target as Mesh;
                //string str = target.vertexCount.ToString() + " verts, " + (object)InternalMeshUtil.GetPrimitiveCount(target) + " tris";
                //int subMeshCount = target.subMeshCount;
                //if (subMeshCount > 1)
                //    str = str + ", " + (object)subMeshCount + " submeshes";
                //int blendShapeCount = target.blendShapeCount;
                //if (blendShapeCount > 1)
                //    str = str + ", " + (object)blendShapeCount + " blendShapes";
                //return str + "\n" + InternalMeshUtil.GetVertexFormat(target);
                return string.Empty;
            }

            public string Print()
            {
                return sb.ToString();
            }
        }

        class QualitySettingsOverrider : IDisposable
        {
            private ShadowQuality _shadows;
            private ShadowResolution _shadowResolution;
            private ShadowProjection _shadowProjection;
            private float _shadowDistance;
            private ShadowmaskMode _shadowmaskMode;

            public QualitySettingsOverrider()
            {
                _shadows = QualitySettings.shadows;
                QualitySettings.shadows = ShadowQuality.All;
                _shadowResolution = QualitySettings.shadowResolution;
                QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
                _shadowProjection = QualitySettings.shadowProjection;
                QualitySettings.shadowProjection = ShadowProjection.CloseFit;
                _shadowDistance = QualitySettings.shadowDistance;
                QualitySettings.shadowDistance = 10;
                _shadowmaskMode = QualitySettings.shadowmaskMode;
                QualitySettings.shadowmaskMode = ShadowmaskMode.DistanceShadowmask;
            }

            public QualitySettingsOverrider(ShadowQuality shadows, ShadowResolution shadowResolution,
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

        class RenderSettingsOverrider : IDisposable
        {
            private AmbientMode _ambientMode;
            private Color _ambientSkyColor;
            private Material _skybox;
            Color _ambientEquatorColor;
            Color _ambientGroundColor;
            float _ambientIntensity;
            Color _ambientLight;
            SphericalHarmonicsL2 _ambientProbe;
            Cubemap _customReflection;
            DefaultReflectionMode _defaultReflectionMode;
            int defaultReflectionResolution;

            public RenderSettingsOverrider(AmbientMode ambientMode, Color ambientSkyColor, Material skybox)
            {
                _ambientMode = RenderSettings.ambientMode;
                _ambientSkyColor = RenderSettings.ambientSkyColor;
                _skybox = RenderSettings.skybox;
                RenderSettings.skybox = skybox;
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientSkyColor = ambientSkyColor;
            }

            public void Dispose()
            {
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientSkyColor = _ambientSkyColor;
                RenderSettings.skybox = _skybox;
            }
        }

        class ShowObjectScope : IDisposable
        {
            private Renderer[] _renderers;

            public ShowObjectScope(GameObject root)
            {
                if (root)
                {
                    _renderers = root.GetComponentsInChildren<Renderer>(true);
                    if (_renderers != null)
                    {
                        if (_renderers.Length > 0)
                        {
                            for (int i = 0; i < _renderers.Length; i++)
                            {
                                _renderers[i].enabled = true;
                            }
                        }
                    }
                }
            }

            public void Dispose()
            {
                if (_renderers != null)
                {
                    if (_renderers.Length > 0)
                    {
                        for (int i = 0; i < _renderers.Length; i++)
                        {
                            _renderers[i].enabled = false;
                        }
                    }
                }
            }
        }

        class CommandBufferManager
        {
            class Blitter
            {
                public Camera camera;
                public CommandBuffer commandBuffer;
                public CameraEvent cameraEvent;
                public RenderTexture rt;
                public Material mat;

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
            public void Add(Camera camera, DepthTextureMode mode, Material mat)
            {
                this._camera = camera;
                //this._mode = mode;
                _camera.depthTextureMode = mode;
                foreach (var blitter in blitterList)
                {
                    blitter.rt = RenderTexture.GetTemporary(camera.targetTexture.width, _camera.targetTexture.height, 24);
                    _camera.AddCommandBuffer(blitter.cameraEvent, blitter.commandBuffer);
                    blitter.commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, blitter.rt, mat);
                    blitter.commandBuffer.Blit(blitter.rt, BuiltinRenderTextureType.CameraTarget);
                }
            }
        }

        class GridLayout
        {
            //private int column = 2;
            private int width;

            public GridLayout(System.Collections.IEnumerable enumerable, int column)
            {
                using (new GUILayout.HorizontalScope())
                {
                    for (int i = 0; i < column + 1; i++)
                    {
                        using (new GUILayout.VerticalScope())
                        {
                        }
                    }
                }
            }
        }

        static class FPS
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

        static class TexUtil
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

        class TransformTreeView : TreeView
        {
            Scene scene;

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
                // Text
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

                    transforms.Add(go.transform);
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

        static class Textures
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

        static class Styles
        {
            public static GUIStyle centeredBoldLabel;

            public static GUIStyle header;
            public static GUIStyle blackHeader;
            public static GUIStyle headerCheckbox;
            public static GUIStyle headerFoldout;

            public static GUIStyle miniHeader;
            //public static GUIStyle miniHeaderCheckbox;
            //public static GUIStyle miniHeaderFoldout;

            public static Texture2D playIcon;
            public static Texture2D checkerIcon;

            public static GUIStyle centeredMiniLabel;

            public static GUIStyle miniButton;
            public static GUIStyle transButton;
            public static GUIStyle miniTransButton;
            public static GUIStyle transFoldout;

            public static GUIStyle tabToolBar;

            public static GUIStyle centeredMinilabel;
            public static GUIStyle centeredMiniBoldLabel;
            public static GUIStyle rightAlignedMinilabel;
            //public static GUIStyle tabToolBar;

            static Styles()
            {
                centeredBoldLabel = new GUIStyle("Label")
                {
                    alignment = TextAnchor.UpperCenter,
                    fontStyle = FontStyle.Bold
                };

                centeredMiniLabel = new GUIStyle()
                {
                    alignment = TextAnchor.UpperCenter
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

                playIcon = (Texture2D) EditorGUIUtility.LoadRequired(
                    "Builtin Skins/DarkSkin/Images/IN foldout act.png");
                checkerIcon = (Texture2D) EditorGUIUtility.LoadRequired("Icons/CheckerFloor.png");

                miniButton = new GUIStyle("miniButton");
                transButton = new GUIStyle("Button");
                transButton.active.background = Texture2D.blackTexture;
                transButton.hover.background = Texture2D.blackTexture;
                transButton.focused.background = Texture2D.blackTexture;
                transButton.normal.background = Texture2D.blackTexture;
                transButton.active.textColor = Color.white;
                transButton.normal.textColor = Color.gray;
                transButton.onActive.background = Texture2D.blackTexture;
                transButton.onFocused.background = Texture2D.blackTexture;
                transButton.onNormal.background = Texture2D.blackTexture;
                transButton.onHover.background = Texture2D.blackTexture;
                transButton.fontStyle = FontStyle.Bold;

                miniTransButton = new GUIStyle("miniButton");
                miniTransButton.active.background = Texture2D.blackTexture;
                miniTransButton.hover.background = Texture2D.blackTexture;
                miniTransButton.focused.background = Texture2D.blackTexture;
                miniTransButton.normal.background = Texture2D.blackTexture;
                miniTransButton.onActive.background = Texture2D.blackTexture;
                miniTransButton.onFocused.background = Texture2D.blackTexture;
                miniTransButton.onNormal.background = Texture2D.blackTexture;
                miniTransButton.onHover.background = Texture2D.blackTexture;
                miniTransButton.active.textColor = Color.white;
                miniTransButton.normal.textColor = Color.gray;
                miniTransButton.normal.background = null;
                miniTransButton.fontStyle = FontStyle.Normal;
                miniTransButton.alignment = TextAnchor.MiddleLeft;

                transFoldout = new GUIStyle("Foldout");
                transFoldout.alignment = TextAnchor.MiddleCenter;
                transFoldout.contentOffset = Vector2.zero;

                tabToolBar = new GUIStyle("dragtab");
                //tabToolBar.onNormal.textColor = Color.white;
                tabToolBar.fontSize = 9;
                tabToolBar.alignment = TextAnchor.MiddleCenter;
                centeredMinilabel = new GUIStyle();
                centeredMinilabel.alignment = TextAnchor.MiddleCenter;
                centeredMiniBoldLabel = new GUIStyle();
                centeredMiniBoldLabel.alignment = TextAnchor.MiddleCenter;
                rightAlignedMinilabel = new GUIStyle();
                rightAlignedMinilabel.alignment = TextAnchor.MiddleRight;
                //tabToolBar = new GUIStyle("dragtab");
                //tabToolBar.onNormal.textColor = Color.white;
                //tabToolBar.fontSize = 9;
                //tabToolBar.alignment = TextAnchor.MiddleCenter;
                //
            }

            static Texture2D staticTex;

            public static GUIStyle GetStyle(GUIStyle baseStyle, Color bgColor, int fontSize, FontStyle fontStyle,
                TextAnchor alignment)
            {
                var dragOKstyle = new GUIStyle(GUI.skin.box)
                    {fontSize = 10, fontStyle = fontStyle, alignment = alignment};
                staticTex = new Texture2D(1, 1);
                staticTex.hideFlags = HideFlags.HideAndDontSave;
                Color[] colors = new Color[1] {bgColor};
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
        }


        struct Fade : System.IDisposable
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

        class Popup : EditorWindow
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
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Add New viewport size", EditorStyles.whiteLargeLabel);
                    if (GUILayout.Button("X", GUILayout.Width(40)))
                        CloseWindow();
                }

                using (new EditorGUILayout.HorizontalScope())
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

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        x = EditorGUILayout.IntSlider("Width", x, 128, 4096);
                        y = EditorGUILayout.IntSlider("Height", y, 128, 4096);
                    }

                    if (GUILayout.Button("Add", GUILayout.Width(80),GUILayout.ExpandHeight(true)))
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

            public static void Log(object message, bool debugOutput)
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

        class ModelAssembler
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

            public void Init(List<ModelGroup> modelGroupList, GenericMenu.MenuFunction dataChangeHandler, GenericMenu.MenuFunction2 targetItemHandler, GenericMenu.MenuFunction2 menuItemHandler)
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
                rol.drawFooterCallback = (position) =>
                {

                };
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
                    Color color = isActive ? Color.white : Color.black;// Color.HSVToRGB(hue, 1.0f, 1.0f);
                    if (pData.enabled.target)
                        EditorGUI.DrawRect(color_area, color * (isActive ? 0.5f : 0.25f));
                    ////1st Row
                    EditorGUI.BeginChangeCheck();
                    ////Draw Header
                    var headerRect = new Rect() { x = listRect.x, y = listRect.y, width = listRect.width, height = lineHeight };
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
                            GUI.Label(position, string.Format("{0}   {1}/{2}", pData.m_Name, pData.SelectedIndex + 1, pData.m_Sources.Count), Styles.centeredMiniLabel);
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
                        bool isSourceExist = (pData.m_Sources.Count > 0) ? (pData.m_Sources[pData.SelectedIndex]) != null : false;

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
                        var style = new GUIStyle(GUI.skin.label) { fontSize = 10, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                        if (GUI.Button(position, pName, style))
                        {
                            if (isSourceExist)
                            {
                                Selection.activeObject = EditorUtility.InstanceIDToObject(pData.m_Sources[pData.SelectedIndex].GetInstanceID());
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
                        pData.SelectedIndex = EditorGUI.IntSlider(position, pData.SelectedIndex + 1, 1, pData.m_Sources.Count) - 1;// EditorGUI.Vector3Field(position, "", pData.Position);

                        ////Default Option
                        using (new EditorGUI.DisabledScope(!isSourceExist))
                        {
                            position.y += space;
                            position.x = listRect.x;
                            position.y += lineHeight;
                            position.width = listRect.width;
                            position.height = miniButtonheight;
                            if (GUI.Button(position, (string.Format("{0}{1}", "Parent - ", string.IsNullOrEmpty(pData.m_TargetPath) ? MODEL_ROOT_NAME : pData.m_TargetPath)), "MiniPopup"))
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
                            pData.m_Options.ResetTransform = GUI.Toggle(position, pData.m_Options.ResetTransform, "Reset Transform", "MiniButton");
                            position.x += position.width;
                            pData.m_Options.m_RenderersOnly = GUI.Toggle(position, pData.m_Options.m_RenderersOnly, "Renderers Only", "MiniButton");

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
                if (rol != null)
                {
                    rol.DoLayoutList();
                }
            }
        }

        class AnimationPlayer
        {
            internal class Animated
            {
                public bool enabled;
                public GameObject gameObject;
                public Animator animator;

                public Animated(GameObject gameObject)
                {
                    this.enabled = true;
                    this.gameObject = gameObject;
                    Animator animator = gameObject.GetComponent<Animator>();
                    if (animator)
                    {
                        this.animator = animator;
                    }
                }
            }

            internal class ClipInfo
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

            internal UnityEditorInternal.ReorderableList reorderableObjectList;
            internal UnityEditorInternal.ReorderableList reorderableClipList;
            //internal UnityEditorInternal.ReorderableList reorderablePlayList;
            internal List<Animated> animatedList = new List<Animated>();
            internal List<AnimationClip> playList = new List<AnimationClip>();
            internal List<ClipInfo> clipInfoList = new List<ClipInfo>();
            private int current;
            internal double time = 0.0f;
            internal float timeSpeed = 1.0f;
            private bool _isOptimized { get; set; }
            internal bool isPlayable { get { return animatedList.Count > 0 && playList.Count > 0; } }
            internal bool isPlaying { get; set; }
            internal bool isLooping { get; set; }
            internal AnimationClip _currentClip { get { return playList[0]; } }
            internal UnityEvent onStopPlaying = new UnityEvent();
            internal ClipInfo currentClipInfo
            {
                get { return clipInfoList.FirstOrDefault(x => x.clip == _currentClip); }
            }

            public AnimationPlayer()
            {
                InitAnimatedList();
                InitClipList();
            }

            private void InitAnimatedList()
            {
                animatedList = new List<Animated>();
                reorderableObjectList = new UnityEditorInternal.ReorderableList(animatedList, typeof(GameObject), true, true, false, false);
                //fields
                reorderableObjectList.showDefaultBackground = false;
                reorderableObjectList.headerHeight = 20;
                reorderableObjectList.elementHeight = 18;
                reorderableObjectList.footerHeight = 0;
                //draw callback
                reorderableObjectList.drawHeaderCallback = (position) =>
                {
                    //var btn20 = position.width * 0.2f;
                    //var btn25 = position.width * 0.25f;
                    //var btn30 = position.width * 0.3f;
                    var btn50 = position.width * 0.5f;
                    position.width = btn50;
                    using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
                    {
                        if (GUI.Button(position, "Add", EditorStyles.miniButtonLeft))
                        {
                            reorderableObjectList.onAddDropdownCallback.Invoke(position, reorderableObjectList);
                        }
                    }
                    position.x += position.width;
                    position.width = btn50;
                    using (new EditorGUI.DisabledScope(reorderableObjectList.index < 0))
                    {
                        if (GUI.Button(position, "Remove", EditorStyles.miniButtonRight))
                        {
                            reorderableObjectList.onRemoveCallback(reorderableObjectList);
                        }
                    }
                };
                reorderableObjectList.drawElementCallback = (position, index, isActive, isFocused) =>
                {
                    float rectWidth = position.width;
                    float rectHeight = position.height;
                    float tglWidth = 15;
                    float btnWidth = 55;
                    position.width = tglWidth;
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        animatedList[index].enabled = EditorGUI.Toggle(position, animatedList[index].enabled);
                        if (check.changed)
                        {
                        }
                    }
                    position.x += position.width;
                    position.width = rectWidth - btnWidth - tglWidth;
                    EditorGUI.LabelField(position, animatedList[index].gameObject.name, EditorStyles.miniBoldLabel);
                    var style = new GUIStyle(EditorStyles.miniLabel);
                    style.alignment = TextAnchor.MiddleRight;
                    style.normal.textColor = Color.gray;
                    bool animatorExist = animatedList[index].animator != null;
                    if (animatorExist)
                        EditorGUI.LabelField(position, animatedList[index].animator.isHuman ? "Humanoid" : "Generic", style);
                    position.x += position.width;
                    position.width = btnWidth;
                    position.height = 16;
                    if (animatorExist)
                    {
                        if (GUI.Button(position, "GetClips", EditorStyles.miniButton))
                        {
                            InitAnimatorAndClips(animatedList[index].animator);
                        }
                    }
                };
                reorderableObjectList.drawFooterCallback = position =>
                {
                    //    var btn20 = position.width * 0.2f;
                    //    var btn25 = position.width * 0.25f;
                    //    var btn30 = position.width * 0.3f;
                    //    var btn50 = position.width * 0.5f;

                    //    position.width = btn50;
                    //    if (GUI.Button(position, "Select All", EditorStyles.miniButtonLeft))
                    //    {
                    //        foreach (var info in animInfoList)
                    //        {
                    //            info.enabled = true;
                    //        }

                    //        RefreshPlayList();
                    //    }

                    //    position.x += position.width;
                    //    position.width = btn50;
                    //    if (GUI.Button(position, "Unselect All", EditorStyles.miniButtonRight))
                    //    {
                    //        foreach (var info in animInfoList)
                    //        {
                    //            info.enabled = false;
                    //        }

                    //        RefreshPlayList();
                    //    }

                    //    position.x += position.width;
                };
                //btn callback
                reorderableObjectList.onAddDropdownCallback = (buttonRect, list) =>
                {
                    var selection = Selection.gameObjects;
                    foreach (var go in selection)
                    {
                        var root = go.transform.root;
                        if (root)
                        {
                            if (!AssetDatabase.Contains(root.gameObject) && !(Enumerable.Any(animatedList, x => x.gameObject == root.gameObject)))
                            {
                                this.animatedList.Add(new Animated(root.gameObject));
                            }
                        }
                    }
                };
                reorderableObjectList.onRemoveCallback = (list) =>
                {
                    reorderableObjectList.index = Mathf.Clamp(reorderableObjectList.index, 0, reorderableObjectList.count - 1);
                    if (animatedList.Count > 0)
                    {
                        animatedList.RemoveAt(reorderableObjectList.index);
                    }
                    reorderableObjectList.index = Mathf.Clamp(reorderableObjectList.index, 0, reorderableObjectList.count - 1);
                };
                reorderableObjectList.onChangedCallback = list => { };
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
                    //var btn20 = position.width * 0.2f;
                    //var btn25 = position.width * 0.25f;
                    //var btn30 = position.width * 0.3f;
                    var btn50 = position.width * 0.5f;
                    position.width = btn50;
                    if (GUI.Button(position, "Add Clip", EditorStyles.miniButtonLeft))
                    {
                        reorderableClipList.onAddDropdownCallback.Invoke(position, reorderableClipList);
                    }

                    position.x += position.width;
                    position.width = btn50;
                    using (new EditorGUI.DisabledScope(reorderableClipList.index < 0))
                    {
                        if (GUI.Button(position, "Remove Clip", EditorStyles.miniButtonRight))
                        {
                            reorderableClipList.onRemoveCallback(reorderableClipList);
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
                        foreach (var info in clipInfoList)
                        {
                            info.enabled = false;
                        }

                        clipInfoList[index].enabled = true;
                        RefreshPlayList();
                        Play();
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
                    if (GUI.Button(position, "Add all to playlist", EditorStyles.miniButtonLeft))
                    {
                        foreach (var info in clipInfoList)
                        {
                            info.enabled = true;
                        }

                        RefreshPlayList();
                    }

                    position.x += position.width;
                    position.width = btn50;
                    if (GUI.Button(position, "Remove all from playlist", EditorStyles.miniButtonRight))
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

            private void InitAnimatorAndClips(Animator animator)
            {
                foreach (var animated in animatedList.ToArray())
                {
                    if (animator)
                    {
                        _isOptimized = !animator.hasTransformHierarchy;
                        //스킨드메쉬 초기화 및 애니메이션 가능하게 디옵티마이즈.
                        //DeOptimizeObject();
                        var clips = AnimationUtility.GetAnimationClips(animated.gameObject);
                        foreach (var clip in clips)
                        {
                            if (Enumerable.Any(clipInfoList, x => x.clip == clip)) continue;
                            clipInfoList.Add(new ClipInfo(clip));
                        }
                    }
                }
            }

            public void AddAnimated(GameObject animated)
            {
                animatedList.Add(new Animated(animated));
            }

            public void AddClip(AnimationClip clip)
            {
                clipInfoList.Add(new ClipInfo(clip));
                RefreshPlayList();
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

                if (animatedList.Count > 0)
                {
                    if (AnimationMode.InAnimationMode())
                    {
                        AnimationMode.BeginSampling();
                        for (var i = 0; i < animatedList.Count; i++)
                        {
                            var animated = animatedList[i];
                            if (animated.enabled)
                            {
                                AnimationMode.SampleAnimationClip(animated.gameObject, _currentClip, (float)time);
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
                    EditorGUILayout.Space();
                    var progressRect =
                        EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight * 1.1f, GUIStyle.none);
                    progressRect = new RectOffset(4, 4, 0, 0).Remove(progressRect);
                    time = GUI.HorizontalSlider(progressRect, (float)time, 0, GetCurrentClipLength(), GUIStyle.none,
                        GUIStyle.none);
                    float length = GetCurrentClipLength();
                    float progress = (float)time / length;
                    EditorGUI.ProgressBar(progressRect, progress,
                        string.Format("{0} : {1}s", GetCurrentClipName(), length.ToString("0.00")));

                    using (var hr = new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUI.DropShadowLabel(hr.rect, string.Format("{0}", currentClipInfo.Print()), EditorStyles.miniLabel);
                        GUIStyle style = new GUIStyle(EditorStyles.miniLabel);
                        style.alignment = TextAnchor.MiddleRight;
                        EditorGUI.DropShadowLabel(hr.rect, string.Format("Speed : {0}X\n Frame : {1}", timeSpeed.ToString("0.0"), (_currentClip.frameRate * progress * _currentClip.length).ToString("000")), style);
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

                        if (GUILayout.Button("0.5x", "ButtonMid", GUILayout.Height(30)))
                        {
                            timeSpeed = 0.5f;

                        }

                        if (GUILayout.Button("1.0x", "ButtonMid", GUILayout.Height(30)))
                        {
                            timeSpeed = 1.0f;

                        }

                        if (GUILayout.Button("2.0x", "ButtonMid", GUILayout.Height(30)))
                        {
                            timeSpeed = 2.0f;

                        }

                        if (GUILayout.Button("+", "ButtonRight", GUILayout.Height(30)))
                        {
                            timeSpeed = Mathf.Min(2, (timeSpeed * 10 + 1f) * 0.1f);

                        }

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
                for (var i = 0; i < animatedList.Count; i++)
                {
                    var animated = animatedList[i];
                    AnimatorUtility.OptimizeTransformHierarchy(animated.gameObject, new string[] { });
                    AnimatorUtility.DeoptimizeTransformHierarchy(animated.gameObject);
                }
            }

            internal void Play()
            {
                for (var i = 0; i < animatedList.Count; i++)
                {
                    var animated = animatedList[i];
                    isPlaying = true;
                    AnimatorUtility.DeoptimizeTransformHierarchy(animated.gameObject);
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
                    for (var i = 0; i < animatedList.Count; i++)
                    {
                        var animated = animatedList[i];
                        AnimatorUtility.OptimizeTransformHierarchy(animated.gameObject, null);
                        ReflectionRestoreToBindPose(animated.gameObject);
                    }
                }
            }

            private void ReflectionRestoreToBindPose(UnityEngine.Object _target)
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
            public static string url = "https://gist.githubusercontent.com/See1Studios/58d573487d07e11e221a7a499545c1f4/raw/23c3a5ebac03b894fd307c86eedec00b5be05e19/AssetStudioVersion.txt";
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
                    outOfDate = (latestMajor > versionNumPrimary || latestMajor == versionNumPrimary && latestMinor > versionNumSecondary);
                    updateCheck = outOfDate ? "See1View is out of date!\nThe latest version is " + update.version : "See1View is up to date!";
                    downloadUrl = update.url;
                }
            }

            internal static IEnumerator Request(string url, Action<string> actionWithText)
            {
                using (UnityWebRequest www = UnityWebRequest.Get(url))
                {
                    www.SendWebRequest();
                    while (!www.isDone)
                    {
                        yield return null;
                    }
                    if (!www.isNetworkError)
                    {
                        actionWithText(www.downloadHandler.text);
                    }
                    else
                    {
                        actionWithText("");
                    }
                }
            }
        }

        #endregion

        #region Properties & Fields

        private See1ViewSettings settings { get { return  See1ViewSettings.instance;} }

        private Data currentData { get { return settings.current; } }

        //Objects
        PreviewRenderUtility _preview;
        GameObject _prefab;
        GameObject _tempPickedObject;
        GameObject _targetGo;
        //GameObject _shadowGo; //Hacky ShadowCaster
        ReflectionProbe _probe;
        Transform _lightPivot;
        private ModelAssembler modelAssembler;

        //Animation
        List<AnimationPlayer> _playerList = new List<AnimationPlayer>();
        List<AnimBool> _isShowingABList = new List<AnimBool>();
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
        int _labelWidth = 95;
        Dictionary<string,SmoothAnimBool> _fadeDic = new Dictionary<string, SmoothAnimBool>();
        //SmoothAnimBool _fade = new SmoothAnimBool();
        TreeView _treeView;
        TreeViewState _treeViewState;
        TargetInfo _targetInfo = new TargetInfo();
        bool _shortcutEnabled;
        SidePanelMode panelMode = SidePanelMode.View;
        Popup _popup;
        bool _guiEnabled = true;
        bool _overlayEnabled = true;
        //Camera & Render
        Transform _camTr;
        Transform _camPivot;
        Vector3 _targetOffset;

        Material _skyMaterial;

        Material _colorMaterial;
        CommandBuffer _colorCommandBuffer;
        bool _colorEnabled;
        private Color _color;

        Material _wireMaterial;
        CommandBuffer _wireCommandBuffer;
        bool _wireFrameEnabled;

        Material _shadowMaterial;
        CommandBuffer _shadowCommandBuffer;
        bool _shadowEnabled;

        Material _depthNormalMaterial;
        CommandBuffer _depthNormalCommandBuffer;
        bool _DepthNormalEnabled;

        Material _gridMaterial;
        CommandBuffer _gridCommandBuffer;
        bool _gridEnabled;

        int _gridSize = 100;
        Color _gridColor = new Color(.5f, .5f, .5f, .5f);

        GizmoMode _gizmoMode = 0;
        int _previewLayer;
        bool _updateFOV;

        Vector2 _destRot = new Vector2(180, 0);
        //Vector2 _destLightRot = new Vector2(180, 0);
        Vector3 _destPivotPos;
        float _destDistance = 1.0f;
        float _dist = 1.0f;
        float _maxDistance = 20.0f;
        float _minDistance = 1.0f;

        bool _autoRotateCamera;
        bool _autoRotateLight;
        int _cameraAutoRotationSpeed;
        int _lightAutoRotationSpeed;

        //PostProcess
#if UNITY_POST_PROCESSING_STACK_V2
        //PostProcessLayer _postLayer;
        //PostProcessVolume _postVolume;
        Editor _ppsEditor;
#endif

        //Info
        GUIContent _viewInfo;
        readonly StringBuilder _sb0 = new StringBuilder();

        #endregion

        #region Unity Events & Callbacks

        void Awake()
        {
            GetPreviewLayerID();
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
            _updateFOV = false;
            SetEditorWindow();
            CreatePreview();
            EditorSceneManager.newSceneCreated += this.OnOpenNewScene;
            Updater.CheckForUpdates();
            var view = settings.current.lastView;
            _destRot = view.rotation;
            _destDistance = view.distance;
            _destPivotPos = view.pivot;
            _preview.cameraFieldOfView = view.fieldOfView;
        }

        void OnDisable()
        {
            settings.current.lastView = new View(_destRot, _destDistance, _destPivotPos, _preview.cameraFieldOfView);
            CleanupPreview();
            EditorSceneManager.newSceneCreated -= this.OnOpenNewScene;
            See1ViewSettings.Save();
            if (_popup) _popup.Close();
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

            FPS.Calculate(_deltaTime);
            SetMaterial();
            UpdateAnimation(_deltaTime);
            Repaint();
        }

        void OnGUI()
        {
            if (_preview == null) return;
            if (!_preview.camera) return;
            GUI.enabled = _guiEnabled;
            EditorGUIUtility.labelWidth = _labelWidth;
            _viewPortRect = IsDocked() ? _rs.full : _rs.center;
            _controlRect = new Rect(_rs.center.position.x, _rs.center.position.y + _rs.center.size.y - 120, _rs.center.size.x, 120);

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

        }

        void OnSelectionChange()
        {
            if (!currentData.autoLoad) return;
            if (Validate(Selection.activeGameObject) == false) return;
            _prefab = Selection.activeGameObject;
            SetModel(_prefab);
        }

        void OnOpenNewScene(Scene scene, NewSceneSetup setup, NewSceneMode mode)
        {
            CleanupPreview();
            CreatePreview();
        }

        #endregion

        #region MainMehods

        void SetEditorWindow()
        {
            _rs = new RectSlicer(this);
            _rs.topTargetHeight = Styles.GetToolbarHeight();
            _rs.bottomTargetHeight = Styles.GetToolbarHeight();
            _rs.leftTargetWidth = 200;
            _rs.rightTargetWidth = 250;
            _rs.openTop.target = true;
            _rs.openBottom.target = false;
            _rs.openLeft.target = false;
            _rs.openRight.target = true;

            var modelFade = new SmoothAnimBool(true);
            modelFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Model", modelFade);

            var controlFade = new SmoothAnimBool(true);
            controlFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Control", controlFade);

            var exportFade = new SmoothAnimBool(true);
            exportFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Export", exportFade);

            var viewFade = new SmoothAnimBool(true);
            viewFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("View", modelFade);

            var envFade = new SmoothAnimBool(true);
            envFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Environment", envFade);

            var lightFade = new SmoothAnimBool(true);
            lightFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Light", lightFade);

            var renderFade = new SmoothAnimBool(true);
            renderFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Render", renderFade);
            var postFade = new SmoothAnimBool(true);
            postFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Post Process", postFade);

            var debugFade = new SmoothAnimBool(true);
            debugFade.valueChanged.AddListener(Repaint);
            _fadeDic.Add("Debug", debugFade);
        }

        void SetModel(GameObject prefab)
        {
            if (!prefab) return;
            if (_targetGo) DestroyImmediate(_targetGo);
            //if (_shadowGo) DestroyImmediate(_shadowGo);
            _targetGo = PrefabUtility.InstantiateAttachedAsset(prefab) as GameObject;
            //_shadowGo = Instantiate(prefab);
            if (_targetGo!=null)
            {
                _targetGo.name = prefab.name;
                //_shadowGo.name = prefab.name + "_Shadow";

                SetFlagsAll(_targetGo, HideFlags.HideAndDontSave);
                //SetFlagsAll(_shadowGo, HideFlags.HideAndDontSave);
                SetLayerAll(_targetGo, _previewLayer);
                //SetLayerAll(_shadowGo, _previewLayer);
                //ShowHideAll(_shadowGo, false);
                _preview.AddSingleGO(_targetGo);
                _targetInfo.Init(_targetGo);

                //etc
                if (currentData.reframeToTarget) FitTargetToViewport();
                currentData.lastTarget = prefab;
                Selection.activeGameObject = _targetGo;
                if (_treeView != null)
                {
                    _treeView.Reload();
                }

                Notice.Log(string.IsNullOrEmpty(_targetInfo.assetPath) ? prefab.name : _targetInfo.assetPath, false);
                SetAnimation(_targetGo, true);
                Repaint();
                //_fade.target = true;
                //_fade.target = false;
            }
        }

        void CreatePreview()
        {
            CleanupPreview();
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

            _preview.ambientColor = Color.gray;


            _skyMaterial = new Material(FindShader("Skybox/Cubemap"));

            _colorCommandBuffer = new CommandBuffer();
            _colorCommandBuffer.name = string.Format("{0} {1}", this.name, "Color");
            _colorMaterial = new Material(FindBuiltinShader("Internal-Colored.shader"));

            _gridCommandBuffer = new CommandBuffer();
            _gridCommandBuffer.name = string.Format("{0} {1}", this.name, "Grid");
            _gridMaterial = new Material(FindShader("Sprites/Default"));

            _wireCommandBuffer = new CommandBuffer();
            _wireCommandBuffer.name = string.Format("{0} {1}", this.name, "WireFrame");
            _wireMaterial = new Material(FindShader("See1View/Wireframe"));
            _wireMaterial = new Material(Shaders.wireFrame);

            _shadowCommandBuffer = new CommandBuffer();
            _shadowCommandBuffer.name = string.Format("{0} {1}", this.name, "Shadow");
            //_shadowMaterial = new Material(FindShader("See1View/PlanarShadow")); //PreviewCamera RT has no stencil buffer. OTL
            _shadowMaterial = new Material(Shaders.planarShadow);

            _depthNormalCommandBuffer = new CommandBuffer();
            _depthNormalCommandBuffer.name = string.Format("{0} {1}", this.name, "DepthNormal");
            _depthNormalMaterial = new Material(Shaders.depthNormal);
            //_depthNormalMaterial = new Material(FindShader("See1View/DepthNormal"));

            var camPivotGo = EditorUtility.CreateGameObjectWithHideFlags("CamPivot", HideFlags.HideAndDontSave);
            _camPivot = camPivotGo.transform;
            _preview.AddSingleGO(camPivotGo);

            var lightPivotGo = EditorUtility.CreateGameObjectWithHideFlags("LightPivot", HideFlags.HideAndDontSave);
            _lightPivot = lightPivotGo.transform;
            _preview.AddSingleGO(lightPivotGo);

            _probe = _preview.camera.gameObject.AddComponent<ReflectionProbe>();
            _probe.mode = ReflectionProbeMode.Custom;
            _probe.size = Vector3.one * 100;
            _probe.cullingMask = ~_previewLayer;
            InitTreeView();
            ResetLight();
            SetPostProcess();
            _prefab = currentData.lastTarget;
            SetModel(_prefab);
        }

        void CleanupPreview()
        {
            if (_camPivot) DestroyImmediate(_camPivot.gameObject);
            if (_lightPivot) DestroyImmediate(_lightPivot.gameObject);

            if (_gridCommandBuffer != null)
            {
                _gridCommandBuffer.Dispose();
                _gridCommandBuffer = null;
            }
            if (_gridMaterial) DestroyImmediate(_gridMaterial);

            if (_wireCommandBuffer != null)
            {
                _wireCommandBuffer.Dispose();
                _wireCommandBuffer = null;
            }
            if (_wireMaterial) DestroyImmediate(_wireMaterial);

            if (_shadowCommandBuffer != null)
            {
                _shadowCommandBuffer.Dispose();
                _shadowCommandBuffer = null;
            }
            if (_shadowMaterial) DestroyImmediate(_shadowMaterial);

            if (_depthNormalCommandBuffer != null)
            {
                _depthNormalCommandBuffer.Dispose();
                _depthNormalCommandBuffer = null;
            }
            if (_depthNormalMaterial) DestroyImmediate(_depthNormalMaterial);

            if (_colorCommandBuffer != null)
            {
                _colorCommandBuffer.Dispose();
                _colorCommandBuffer = null;
            }
            if (_colorMaterial) DestroyImmediate(_colorMaterial);

            if (_skyMaterial) DestroyImmediate(_skyMaterial);
            //if (_shadowGo) DestroyImmediate(_shadowGo);
            if (_preview != null)
            {
                RenderTexture.active = null;
                _preview.Cleanup(); //Handle.SetCamera 에서 RenderTexure.active 관련 warning 발생
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
            }
        }

        void SetMaterial()
        {
            if (_skyMaterial)
            {
                _skyMaterial.SetTexture("_Tex", currentData.cubeMap);
                //_skyMaterial.SetFloat("_Rotation", _preview.lights[0].transform.rotation.eulerAngles.y);
            }
            if (_colorMaterial)
            {
                _colorMaterial.SetColor("_Color", _color);
            }
            if (_gridMaterial)
            {
                _gridMaterial.SetColor("_Color", _gridColor);
            }

            if (_wireMaterial)
            {
                _wireMaterial.SetColor("_LineColor", currentData.wireLineColor);
                _wireMaterial.SetColor("_FillColor", currentData.wireFillColor);
                _wireMaterial.SetFloat("_WireThickness", currentData.wireThickness);
                _wireMaterial.SetFloat("UseDiscard", currentData.wireUseDiscard);
            }

            if (_shadowMaterial)
            {
                _shadowMaterial.SetColor("_ShadowColor", currentData.planeShadowColor);
                _shadowMaterial.SetFloat("_PlaneHeight", _targetInfo.bounds.min.y);
            }
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
            if(_targetGo) SetFlagsAll(_targetGo.gameObject, HideFlags.None);
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();        
        }

        private void DataChanged()
        {
            //_list = AS_PartList.Create(settings.currentData, PartChanged, TargetItemHandler, MenuItemHandler);
            //Selection.activeObject = settings.currentData;
            _prefab = settings.current.lastTarget;
            SetModel(_prefab);
            SetPostProcess();
            See1ViewSettings.SetDirty();
        }

        #endregion

        #region CommandBuffer and Render


        void SetGridBuffer(bool set)
        {
            RemoveBufferFromAllEvent(_preview.camera, _gridCommandBuffer);
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
            RemoveBufferFromAllEvent(_preview.camera, buffer);
            //_preview.camera.RemoveCommandBuffer(cameraEvent, buffer);
            buffer.Clear();
            if (_targetGo && set)
            {
                _preview.camera.AddCommandBuffer(cameraEvent, buffer);
                var renderers = _targetGo.GetComponentsInChildren<Renderer>();
                for (var i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];

                    var smr = renderer as SkinnedMeshRenderer;
                    var mr = renderer as MeshRenderer;
                    if (smr)
                    {
                        for (int j = 0; j < smr.sharedMesh.subMeshCount; j++)
                        {
                            int idx = j;
                            buffer.DrawRenderer(renderer, mat, idx, -1);
                        }
                    }

                    else if (mr)
                    {
                        var mf = mr.GetComponent<MeshFilter>();
                        for (int j = 0; j < mf.sharedMesh.subMeshCount; j++)
                        {
                            int idx = j;
                            buffer.DrawRenderer(renderer, mat, idx, -1);
                        }
                    }
                }
            }
        }

        void SetCameraTargetBlitBuffer(CameraEvent cameraEvent, CommandBuffer buffer, Material mat, bool set)
        {
            RemoveBufferFromAllEvent(_preview.camera, buffer);
            buffer.Clear();
            if (_targetGo && set)
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

        private void RemoveBufferFromAllEvent(Camera camera, CommandBuffer buffer)
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

        void SetPostProcess()
        {
#if UNITY_POST_PROCESSING_STACK_V2
            var postLayer = _preview.camera.gameObject.GetComponent<PostProcessLayer>();
            var postVolume = _preview.camera.gameObject.GetComponent<PostProcessVolume>();

            if (currentData.enablePostProcess && currentData.profile)
            {
                if (!postLayer) postLayer = _preview.camera.gameObject.AddComponent<PostProcessLayer>();
                postLayer.antialiasingMode = true
                    ? PostProcessLayer.Antialiasing.SubpixelMorphologicalAntialiasing
                    : PostProcessLayer.Antialiasing.None;
                if (!postVolume) postVolume = _preview.camera.gameObject.AddComponent<PostProcessVolume>();
                postLayer.volumeLayer = 1;
                postLayer.antialiasingMode = PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                postLayer.fastApproximateAntialiasing.fastMode = true;
                postLayer.fastApproximateAntialiasing.keepAlpha = true;
                postVolume.isGlobal = true;
                postVolume.profile = currentData.profile as PostProcessProfile;
                //if (!_ppsEditor) _ppsEditor = Editor.CreateEditor(settings.profile);
            }
            else
            {
                if (postVolume) DestroyImmediate(postVolume);
                if (postLayer) DestroyImmediate(postLayer);
                //if (_ppsEditor) DestroyImmediate(_ppsEditor);
            }
#endif
        }

        Texture2D RenderToTexture(int multiplyer = 1, bool alpha = false)
        {
            int w = (int)_viewPortRect.size.x * multiplyer * currentData.viewportMultiplier;
            int h = (int)_viewPortRect.size.y * multiplyer * currentData.viewportMultiplier;
            _preview.BeginPreview(new Rect(_viewPortRect.position, new Vector2(w, h)), GUIStyle.none);
            using (new QualitySettingsOverrider())
            {
                //using (new ShowObjectScope(_shadowGo))
                //{
                    using (new RenderSettingsOverrider(AmbientMode.Flat, currentData.ambientSkyColor, _skyMaterial))
                    {
                        if (alpha)
                        {
                            CameraClearFlags clearFlags = _preview.camera.clearFlags;
                            Color backgroundColor = _preview.camera.backgroundColor;
                            _preview.camera.clearFlags = CameraClearFlags.Color;
                            _preview.camera.backgroundColor = Color.clear;
                            _preview.Render(currentData.enableSRP, _updateFOV);
                            _preview.camera.clearFlags = clearFlags;
                            _preview.camera.backgroundColor = backgroundColor;
                        }
                        else
                        {
                            _preview.Render(currentData.enableSRP, _updateFOV);
                        }
                    }
                //}
            }

            Texture tex = _preview.EndPreview();
            RenderTexture temp = RenderTexture.GetTemporary(w / currentData.viewportMultiplier,
                h / currentData.viewportMultiplier, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
            Graphics.Blit(tex, temp);
            GL.sRGBWrite = false;
            RenderTexture.active = temp;
            Texture2D tex2D = new Texture2D(temp.width, temp.height, alpha ? TextureFormat.ARGB32 : TextureFormat.RGB24, false, true);
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
            Texture2D tex = RenderToTexture((int)currentData.captureMultiplier, currentData.screenShotAlpha);
            string baseName = _targetGo ? _targetGo.name : "Blank";
            string savedPath = SaveAsFile(tex, Directory.GetParent(Application.dataPath).ToString() + "/Screenshots", baseName, settings.current.fileExistsMode);
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
            _popup = ScriptableObject.CreateInstance<Popup>() as Popup;
            _popup.Init(this, v2 => { AddViewportSize(v2); }, () => { _guiEnabled = true; });
            _popup.ShowPopup();
            _guiEnabled = false;
        }

        #endregion

        #region Animation

        public void SetAnimation(GameObject animatedRoot, bool reset)
        {
            //기존 수집된 애니메이터에 문제가 있는지 검사 (모델 옵션이 바뀜에 따라 있던 애니메이터가 없어지는 경우가 생김.
            if (_playerList.Any(x => x.animatedList.Any( ani =>ani.gameObject == null)))
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
                _isShowingABList.Clear();
                Animator[] animators = animatedRoot.GetComponentsInChildren<Animator>();
                for (int i = 0; i < animators.Length; i++)
                {
                    Animator animator = animators[i];
                    AnimationPlayer player = new AnimationPlayer();
                    player.AddAnimated(animator.gameObject);
                    player.onStopPlaying = onStopPlaying;
                    _playerList.Add(player);
                    _isShowingABList.Add(new AnimBool((i == 0) ? true : false));
                }
            }
        }

        public void UpdateAnimation(double delta)
        {
            for (int i = 0; i < _playerList.Count; i++)
            {
                _playerList[i].Update(delta);
            }
        }
        #endregion

        #region GUI

        void OnGUI_Top(Rect r)
        {
            //if (IsDocked())
            //    EditorGUI.DrawRect(r, GetGrayscaleInverted(_preview.camera.backgroundColor) * 0.5f);
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
                    GUILayout.FlexibleSpace();

                    int idx = See1ViewSettings.instance.dataIndex;
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        settings.dataIndex = (int)EditorGUILayout.Popup(settings.dataIndex, See1ViewSettings.dataNames, EditorStyles.toolbarPopup);
                        if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(20)))
                        {
                            string newName = typeof(Data).Name;
                            settings.Add(newName);
                        }
                        using (new EditorGUI.DisabledGroupScope(settings.dataList.Count == 1))
                        {
                            if (GUILayout.Button("-", EditorStyles.toolbarButton, GUILayout.Width(20)))
                            {
                                if (EditorUtility.DisplayDialog("Confirm", string.Format("{0}{1}{2}", "Delete ", currentData.name, "?"), "Ok", "Cancel"))
                                {
                                    settings.RemoveCurrent();
                                }
                            }
                        }
                        if (check.changed)
                        {
                            if (idx != settings.dataIndex)
                            {
                                DataChanged();
                                //SidebarChanged();
                            }
                        }
                    }

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
            Rect renderRectScaled = new Rect(r.position, r.size * currentData.viewportMultiplier);
            GUIStyle style = GUIStyle.none;
            using (new QualitySettingsOverrider())
            {
                //using (new ShowObjectScope(_shadowGo))
                //{
                    _preview.BeginPreview(renderRectScaled, style);
                    using (new RenderSettingsOverrider(AmbientMode.Flat, currentData.ambientSkyColor, _skyMaterial))
                    {
                        GL.wireframe = true;
                        _preview.DrawMesh(Grid.Get(100), Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one), _gridMaterial, 0);
                        GL.wireframe = false;
                        _preview.Render(currentData.enableSRP, _updateFOV);
                        //_preview.Render(settings.enableSRP, _updateFOV);
                    }
                    //Texture image = _preview.EndPreview();
                    //GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                    //GUI.DrawTexture(r, image, ScaleMode.StretchToFill, true);
                    //GL.sRGBWrite = false;
                    //UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    _preview.EndAndDrawPreview(_viewPortRect);
                //}
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
                Rect top = new Rect(area.x,area.y,area.width, EditorGUIUtility.singleLineHeight);
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

            using (Fade.Do(_rs.openRight.faded))
            {
                using (new GUILayout.AreaScope(area))
                {
                    using (var check = new EditorGUI.ChangeCheckScope())
                    {
                        panelMode = (SidePanelMode) GUILayout.Toolbar((int) panelMode,
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
                            case SidePanelMode.Tools:
                                OnGUI_Tools();
                                break;
                        }
                    }
                }
            }
        }

        void OnGUI_View()
        {
            _fadeDic["Control"].target = Styles.Foldout(_fadeDic["Control"].target, "Control");
            using (var fade = new EditorGUILayout.FadeGroupScope(_fadeDic["Control"].faded))
            {
                if (fade.visible)
                {
                    currentData.rotSpeed = EditorGUILayout.IntSlider("Rotate Speed", currentData.rotSpeed, 1, 5);
                    currentData.zoomSpeed = EditorGUILayout.IntSlider("Zoom Speed", currentData.zoomSpeed, 1, 5);
                    currentData.panSpeed = EditorGUILayout.IntSlider("Pan Speed", currentData.panSpeed, 1, 5);
                    currentData.smoothFactor = EditorGUILayout.IntSlider("Smoothness", currentData.smoothFactor, 1, 5);
                    _targetOffset = EditorGUILayout.Vector3Field("Offset", _targetOffset);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _autoRotateCamera = GUILayout.Toggle(_autoRotateCamera, "Rotate Camera",
                            EditorStyles.miniButton,
                            GUILayout.Width(_labelWidth));
                        _cameraAutoRotationSpeed = EditorGUILayout.IntSlider(_cameraAutoRotationSpeed, -10, 10);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        _autoRotateLight = GUILayout.Toggle(_autoRotateLight, "Rotate Light", EditorStyles.miniButton,
                            GUILayout.Width(_labelWidth));
                        _lightAutoRotationSpeed = EditorGUILayout.IntSlider(_lightAutoRotationSpeed, -10, 10);
                    }
                }
            }

            Styles.Foldout(true, "Render");

            //GUILayout.Label(string.Format("Name : {0}"), EditorStyles.miniLabel);
            GUILayout.Label(string.Format("Size : {0} x {1}", _viewPortRect.width * currentData.captureMultiplier, _viewPortRect.height * currentData.captureMultiplier), EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                currentData.captureMultiplier = EditorGUILayout.IntSlider(currentData.captureMultiplier, 1, 8);
                currentData.screenShotAlpha = GUILayout.Toggle(currentData.screenShotAlpha, "Alpha", EditorStyles.miniButton);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        currentData.fileExistsMode = (FileExistsMode)GUILayout.Toolbar((int)currentData.fileExistsMode, Enum.GetNames(typeof(FileExistsMode)), EditorStyles.miniButton);
                    }

                    currentData.openSavedImage = GUILayout.Toggle(currentData.openSavedImage, "Open Saved Image",
                        EditorStyles.miniButton);
                    if (GUILayout.Button("Open Saved Folder", EditorStyles.miniButton))
                    {
                        EditorUtility.OpenWithDefaultApp(Directory.GetParent(Application.dataPath).ToString() +
                                                         "/Screenshots");
                    }
                }
                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("Render", GUILayout.Width(80),GUILayout.Height(48)))
                {
                    RenderAndSaveFile();
                }
                GUI.backgroundColor = Color.white;
            }
            if (!EditorUserBuildSettings.activeBuildTarget.ToString().Contains("Standalone") && currentData.screenShotAlpha && currentData.enablePostProcess)
            {
                EditorGUILayout.HelpBox("Only standalone platforms supports alpha blended post process ", MessageType.Warning);
            }


            Styles.Foldout(true, "View");
            //_targetOffset = EditorGUILayout.Vector3Field("Target Offset", _targetOffset);
            using (new EditorGUILayout.HorizontalScope())
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

            using (new EditorGUILayout.HorizontalScope())
            {
                _preview.camera.fieldOfView =
                    EditorGUILayout.IntSlider("Field Of View", (int)_preview.camera.fieldOfView, 10, 90);
                _preview.camera.orthographic =
                    GUILayout.Toggle(_preview.camera.orthographic, _preview.camera.orthographic ? "O" : "P",
                        EditorStyles.miniButton, GUILayout.Width(20));
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Current", EditorStyles.miniButtonLeft))
                {
                    currentData.viewList.Add(new View(_destRot, _destDistance, _destPivotPos,
                        _preview.cameraFieldOfView));
                }

                using (new EditorGUI.DisabledScope(!Selection.activeGameObject))
                {
                    if (GUILayout.Button("Add From Camera", EditorStyles.miniButtonRight))
                    {
                        var camera = Selection.activeGameObject.GetComponent<Camera>();
                        if (camera)
                        {
                            currentData.viewList.Add(new View(camera));
                        }
                    }
                }
            }

            using (var scope = new EditorGUILayout.HorizontalScope())
            {
                //float width = 0;
                //if (Event.current.type == EventType.Repaint)
                //{
                    //Rect left = new Rect(scope.rect.position, new Vector2(scope.rect.size.x * 0.5f, scope.rect.size.y));
                    //EditorGUI.DrawRect(left, Color.red);
                    //Rect right = new Rect(new Vector2(scope.rect.position.x + left.size.x, scope.rect.position.y),                        left.size);
                    //EditorGUI.DrawRect(right, Color.green);
                    //width = scope.rect.size.x * 0.5f;
                //}

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(110)))
                {
                    for (var i = 0; i < currentData.viewList.Count; i = i + 2)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var view = currentData.viewList[i];
                            if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(20)))
                            {
                                view.rotation = _destRot;
                                view.distance = _destDistance;
                                view.pivot = _destPivotPos;
                                view.fieldOfView = _preview.cameraFieldOfView;
                                Notice.Log(string.Format("Current view saved to slot {0}",i.ToString()),false);
                            }

                            if (GUILayout.Button(string.Format("{0}.{1}",i.ToString(),view.name), EditorStyles.miniButtonMid))
                            {
                                ApplyView(i);
                            }

                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(20)))
                            {
                                currentData.viewList.Remove(view);
                                Notice.Log(string.Format("Slot {0} Removed", i.ToString()), false);
                            }
                        }
                    }
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    for (var i = 1; i < currentData.viewList.Count; i = i + 2)
                    {

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var view = currentData.viewList[i];
                            if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(20)))
                            {
                                view.rotation = _destRot;
                                view.distance = _destDistance;
                                view.pivot = _destPivotPos;
                                view.fieldOfView = _preview.cameraFieldOfView;
                                Notice.Log(string.Format("Current view saved to slot {0}", i.ToString()), false);
                            }

                            if (GUILayout.Button(string.Format("{0}.{1}", i.ToString(), view.name), EditorStyles.miniButtonMid))
                            {
                                ApplyView(i);
                            }


                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(20)))
                            {
                                currentData.viewList.Remove(view);
                                Notice.Log(string.Format("Slot {0} Removed", i.ToString()), false);
                            }
                        }
                    }
                }
            }

            GUILayout.Label("Viewport Sizes", EditorStyles.miniLabel);
            using (new EditorGUILayout.HorizontalScope())
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

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    for (var i = 0; i < currentData.viewportSizes.Count; i = i + 2)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var size = currentData.viewportSizes[i];
                            if (GUILayout.Button(string.Format("{0}x{1}", size.x.ToString("#"), size.y.ToString("#")),
                                EditorStyles.miniButtonLeft))
                            {
                                ResizeWindow(size);
                            }

                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
                            {
                                currentData.viewportSizes.Remove(size);
                            }
                        }
                    }
                }

                using (new EditorGUILayout.VerticalScope())
                {
                    for (var i = 1; i < currentData.viewportSizes.Count; i = i + 2)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var size = currentData.viewportSizes[i];
                            if (GUILayout.Button(string.Format("{0}x{1}", size.x.ToString("#"), size.y.ToString("#")),
                                EditorStyles.miniButtonLeft))
                            {
                                ResizeWindow(size);
                            }

                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
                            {
                                currentData.viewportSizes.Remove(size);
                            }
                        }
                    }
                }
            }

            Styles.Foldout(true, "Environment");

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Background");
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    bool isSky = (currentData.clearFlag == ClearFlags.Sky);
                    isSky = !GUILayout.Toggle(!isSky, "Color", EditorStyles.miniButtonLeft);
                    isSky = GUILayout.Toggle(isSky, "Environment", EditorStyles.miniButtonRight);
                    if (check.changed)
                    {
                        currentData.clearFlag = isSky ? ClearFlags.Sky : ClearFlags.Color;

                    }
                }
            }

            ColorPickerHDRConfig config = new ColorPickerHDRConfig(0, 2, 0, 2);
            if (currentData.clearFlag == ClearFlags.Sky)
            {
                using (new EditorGUI.DisabledGroupScope(true))
                {
                    EditorGUILayout.ObjectField("Material", _skyMaterial, typeof(Material), false);
                }
                _preview.camera.clearFlags = CameraClearFlags.Skybox;
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    currentData.bgColor = EditorGUILayout.ColorField(new GUIContent("Color"), currentData.bgColor,true,true,true, config);
                    _preview.camera.backgroundColor = currentData.bgColor;
                    _preview.camera.clearFlags = CameraClearFlags.SolidColor;
                }
            }

            _preview.ambientColor = currentData.ambientSkyColor =
                EditorGUILayout.ColorField(new GUIContent("Ambient"), currentData.ambientSkyColor, true, true, true, config);
            _probe.customBakedTexture = currentData.cubeMap = (Cubemap)EditorGUILayout.ObjectField("Environment", currentData.cubeMap, typeof(Cubemap), false);

            currentData.CubeMapMipMapBias = EditorGUILayout.IntSlider("Bias", (int)currentData.CubeMapMipMapBias, 0, 10);

            //settings.enableSRP = GUILayout.Toggle(settings.enableSRP, "Enable Scriptable Render Pipeline", EditorStyles.miniButton);

            Styles.Foldout(true, "Light");
            using (var lightCheck = new EditorGUI.ChangeCheckScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    currentData.enableShadows =
                        GUILayout.Toggle(currentData.enableShadows, "Shadow", EditorStyles.miniButton,GUILayout.Width(50));

                    EditorGUIUtility.labelWidth = 40;
                    currentData.shadowBias = EditorGUILayout.Slider("Bias", currentData.shadowBias, 0, 1);
                    EditorGUIUtility.labelWidth = _labelWidth;
                }

                for (var i = 0; i < _preview.lights.Length; i++)
                {
                    var previewLight = _preview.lights[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUIUtility.labelWidth = 40;
                        GUILayout.Label(string.Format("Light{0}",i.ToString()),EditorStyles.miniLabel);
                        previewLight.color = EditorGUILayout.ColorField(new GUIContent(""),previewLight.color, true, true, true, config, GUILayout.Width(50));
                        previewLight.intensity = EditorGUILayout.Slider("", previewLight.intensity, 0, 2);
                        EditorGUIUtility.labelWidth = _labelWidth;
                    }

                    if (lightCheck.changed)
                    {
                        previewLight.shadows = currentData.enableShadows ? LightShadows.Soft : LightShadows.None;
                        previewLight.shadowBias = currentData.shadowBias;
                    }
                }
            }




            Styles.Foldout(true, "Render");

            currentData.viewportMultiplier = GUILayout.Toggle((currentData.viewportMultiplier == 2), "Enable Viewport Supersampling", EditorStyles.miniButton) ? 2 : 1;

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var cbCheck = new EditorGUI.ChangeCheckScope())
                {
                    _gridEnabled = GUILayout.Toggle(_gridEnabled, "Grid", EditorStyles.miniButton, GUILayout.Width(_labelWidth));
                    //_gridSize = EditorGUILayout.IntSlider(_gridSize, 0, 100);
                    if (cbCheck.changed)
                    {
                        SetGridBuffer(_gridEnabled);
                    }
                }
                _gridColor = EditorGUILayout.ColorField(_gridColor);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var cbCheck = new EditorGUI.ChangeCheckScope())
                {
                    _wireFrameEnabled = GUILayout.Toggle(_wireFrameEnabled, "WireFrame", EditorStyles.miniButton,
                        GUILayout.Width(_labelWidth));
                    if (cbCheck.changed)
                    {
                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _wireCommandBuffer, _wireMaterial,
                            _wireFrameEnabled);
                    }
                }

                currentData.wireLineColor = EditorGUILayout.ColorField(currentData.wireLineColor);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var cbCheck = new EditorGUI.ChangeCheckScope())
                {
                    _colorEnabled = GUILayout.Toggle(_colorEnabled, "Color", EditorStyles.miniButton,
                        GUILayout.Width(_labelWidth));
                    if (cbCheck.changed)
                    {
                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _colorCommandBuffer, _colorMaterial,
                            _colorEnabled);
                    }
                }

                _color = EditorGUILayout.ColorField(_color);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var psCheck = new EditorGUI.ChangeCheckScope())
                {
                    currentData.enablePlaneShadows = GUILayout.Toggle(currentData.enablePlaneShadows, "PlaneShadow",
                        EditorStyles.miniButton, GUILayout.Width(_labelWidth));
                    if (psCheck.changed)
                    {
                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _shadowCommandBuffer, _shadowMaterial,
                            currentData.enablePlaneShadows);
                    }
                }

                currentData.planeShadowColor = EditorGUILayout.ColorField(currentData.planeShadowColor);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (var cbCheck = new EditorGUI.ChangeCheckScope())
                {
                    _DepthNormalEnabled = GUILayout.Toggle(_DepthNormalEnabled, "Normal Visualize",
                        EditorStyles.miniButton);
                    if (cbCheck.changed)
                    {
                        _preview.camera.depthTextureMode =
                            _DepthNormalEnabled ? DepthTextureMode.DepthNormals : DepthTextureMode.None;
                        SetCameraTargetBlitBuffer(CameraEvent.AfterForwardOpaque, _depthNormalCommandBuffer, _depthNormalMaterial, _DepthNormalEnabled);
                        //if (_DepthNormalEnabled)
                        //{
                        //    _preview.camera.SetReplacementShader(_normalMaterial.shader, string.Empty);
                        //}
                        //else
                        //{
                        //    _preview.camera.ResetReplacementShader();
                        //}
                    }
                }
            }

            Styles.Foldout(true, "Post Process");
#if UNITY_POST_PROCESSING_STACK_V2

            using (var check = new EditorGUI.ChangeCheckScope())
            {
                currentData.enablePostProcess = GUILayout.Toggle(currentData.enablePostProcess, "Enable Post Processing",
                    EditorStyles.miniButton);
                if (currentData.enablePostProcess)
                    currentData.profile =
                        (PostProcessProfile)EditorGUILayout.ObjectField("", currentData.profile,
                            typeof(PostProcessProfile), false);
                if (check.changed)
                {
                    SetPostProcess();
                }
            }

            //if (_ppsEditor) _ppsEditor.OnInspectorGUI();
#else
                    EditorGUILayout.HelpBox("To use Post Process, add the Post Process Stack V2 package to your project.", MessageType.Info);
#endif

            Styles.Foldout(true, "Gizmos");
            using (new EditorGUILayout.HorizontalScope())
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

            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
        }

        void OnGUI_Model()
        {
            _fadeDic["Model"].target = Styles.Foldout(_fadeDic["Model"].target, "Model");
            using (var fade = new EditorGUILayout.FadeGroupScope(_fadeDic["Model"].faded))
            {
                if (fade.visible)
                {
                    GUI.backgroundColor = Color.cyan;
                    currentData.autoLoad = GUILayout.Toggle(currentData.autoLoad, "Auto Load Selection", "Button",
                        GUILayout.Height(32));
                    GUI.backgroundColor = Color.white;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        currentData.reframeToTarget = GUILayout.Toggle(currentData.reframeToTarget, "Reframe Target",
                            EditorStyles.miniButtonLeft);
                        currentData.recalculateBound = GUILayout.Toggle(currentData.recalculateBound, "Recalculate Bound",
                            EditorStyles.miniButtonRight);
                    }

                    _prefab = EditorGUILayout.ObjectField(_prefab, typeof(GameObject), false) as GameObject;

                    if (currentData.autoLoad && EditorGUIUtility.GetObjectPickerControlID() != 0
                    ) //어떤 object picker 가 안 열렸을 때 0
                    {
                        _tempPickedObject = EditorGUIUtility.GetObjectPickerObject() as GameObject;
                        if (_prefab != _tempPickedObject)
                        {
                            _prefab = _tempPickedObject;
                            SetModel(_prefab);
                            _tempPickedObject = null;
                        }
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Primitives", GUILayout.Width(80), GUILayout.Height(32)))
                        {
                            int number = UnityEngine.Random.Range(1, 4);
                            var primitive = GameObject.CreatePrimitive((PrimitiveType)number);
                            SetModel(primitive);
                            DestroyImmediate(primitive);
                        }

                        if (GUILayout.Button("Create", GUILayout.Height(32)))
                        {
                            if (_prefab)
                            {
                                SetModel(_prefab);
                            }
                        }
                    }
                }
            }
            if (modelAssembler != null)
            {
                modelAssembler.OnGUI();
            }

            Styles.Foldout(true, "Model");
            using (new EditorGUI.DisabledGroupScope(true))
            {
                EditorGUILayout.ObjectField("", _prefab, typeof(GameObject), false);
            }
            Styles.Foldout(true, "Materials");
            using (new EditorGUI.DisabledGroupScope(true))
            {
                foreach (var mat in _targetInfo.materials)
                {
                    EditorGUILayout.ObjectField("", mat, typeof(Material), false);
                }
            }
        }

        void OnGUI_Animation()
        {
            for (int a = 0; a < _playerList.Count; a++)
            {
                var player = _playerList[a];
                //Styles.Foldout(true, "Playlist");
                //foreach (var animationClip in player.playList)
                //{
                //    EditorGUILayout.LabelField(animationClip.name, EditorStyles.miniLabel);
                //    if (animationClip.name == player.GetCurrentClipName())
                //    {
                //        var progressRect = GUILayoutUtility.GetLastRect();
                //        EditorGUI.ProgressBar(progressRect, (float) player.time / player.GetCurrentClipLength(),
                //            string.Format("{0} : {1}s", player.GetCurrentClipName(),
                //                player.GetCurrentClipLength().ToString("0.00")));
                //    }
                //}

                _isShowingABList[a].target = Styles.Foldout(_isShowingABList[a].target, string.Format("Player {0}", a.ToString()));
                for (int b = 0; b < player.animatedList.Count; b++)
                {
                    using (var fade = new EditorGUILayout.FadeGroupScope(_isShowingABList[a].faded))
                    {
                        if (fade.visible)
                        {
                            //using (var check = new EditorGUI.ChangeCheckScope())
                            //{
                            //    player.animator.runtimeAnimatorController =
                            //        EditorGUILayout.ObjectField(player.animator.runtimeAnimatorController,
                            //            typeof(AnimatorController), false) as AnimatorController;
                            //    if (check.changed)
                            //    {
                            //        player.InitAnimatorAndClips();
                            //    }
                            //}
                            using (new EditorGUILayout.VerticalScope())
                            {
                                //player.reorderableObjectList.DoLayoutList();
                                player.reorderableClipList.DoLayoutList();
                            }
                        }
                    }
                    EditorGUILayout.Space();
                }
            }
        }

        void OnGUI_AnimationControl(Rect r)
        {
            if (!_overlayEnabled) return;
            Rect area = new RectOffset(4, 4, 4, 4).Remove(r);
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
            if (_targetInfo.particleSystems.Length == 0) return;
            Rect area = new RectOffset(4, 4, 4, 4).Remove(r);
            //EditorGUI.DrawRect(psRect, GetInvertedLuminaceGrayscaleColor(_preview.camera.backgroundColor) * 0.5f);
            using (new GUILayout.AreaScope(area))
            {
                ParticleSystem particleSystem = _targetInfo.particleSystems[0];
                GUIStyle style = new GUIStyle();
                var progressRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, style);
                //EditorGUI.DrawRect(progressRect, Color.red);
                //particleSystem.main.time = GUI.HorizontalSlider(progressRect, (float)_player.time, 0, _player.GetCurrentClipLength(), style, style);
                float length = particleSystem.main.duration;
                EditorGUI.ProgressBar(progressRect, (float)particleSystem.time / length, string.Format("{0} : {1}s", particleSystem.name, length.ToString("0.00")));

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Play", "ButtonLeft", GUILayout.Width(50), GUILayout.Height(30)))
                    {
                        Selection.activeGameObject = _targetGo;
                        foreach (var ps in _targetInfo.particleSystems)
                        {
                            ps.Play();
                        }
                    }

                    if (GUILayout.Button("Restart", "ButtonMid", GUILayout.Width(50), GUILayout.Height(30)))
                    {
                        Selection.activeGameObject = _targetGo;
                        foreach (var ps in _targetInfo.particleSystems)
                        {
                            ps.Stop();
                            ps.Play();
                        }
                    }

                    if (GUILayout.Button("Stop", "ButtonMid", GUILayout.Width(50), GUILayout.Height(30)))
                    {
                        Selection.activeGameObject = _targetGo;
                        foreach (var ps in _targetInfo.particleSystems)
                        {
                            ps.Clear();
                        }
                    }

                    if (GUILayout.Button("Pause", "ButtonRight", GUILayout.Width(50), GUILayout.Height(30)))
                    {
                        Selection.activeGameObject = _targetGo;
                        foreach (var ps in _targetInfo.particleSystems)
                        {
                            ps.Pause();
                        }
                    }

                    GUILayout.FlexibleSpace();
                }
            }
        }

        void OnGUI_Tools()
        {
            Styles.Foldout(true, "Settings");
            //settings.autoLoad = GUILayout.Toggle(settings.autoLoad, "Auto Load Selection", "Button", GUILayout.Height(32));

            using (new EditorGUI.DisabledScope(!EditorPrefs.HasKey(See1ViewSettings.key)))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Load", EditorStyles.miniButtonLeft))
                    {
                        var path = (EditorUtility.OpenFilePanel("Load Settings File", Application.dataPath, "json"));
                        if (!string.IsNullOrEmpty(path))
                        {
                            var json = File.ReadAllText(path);
                            if (!string.IsNullOrEmpty(json))
                            {
                                JsonUtility.FromJsonOverwrite(json, settings);
                                See1ViewSettings.Save();
                            }
                        }
                    }
                    if (GUILayout.Button("Save", EditorStyles.miniButtonMid))
                    {
                        See1ViewSettings.Save();
                    }
                    if (GUILayout.Button("Delete", EditorStyles.miniButtonRight))
                    {
                        See1ViewSettings.DeleteAll();
                    }
                }
            }
            for (var i = 0; i < settings.dataList.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var data = settings.dataList[i];
                    EditorGUILayout.PrefixLabel(i.ToString());
                    data.name = EditorGUILayout.TextField(data.name);
                }
            }

            Styles.Foldout(true, "View");
            for (var i = 0; i < settings.current.viewList.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var view = settings.current.viewList[i];
                    EditorGUILayout.PrefixLabel(i.ToString());
                    view.name = EditorGUILayout.TextField(view.name);
                }
            }

            Styles.Foldout(true, "Camera Target");
            EditorGUILayout.ObjectField(_preview.camera.targetTexture, typeof(RenderTexture),false);
            EditorGUILayout.ObjectField(_wireMaterial, typeof(Material), false);
            EditorGUILayout.ObjectField(_shadowMaterial, typeof(Material), false);
            EditorGUILayout.ObjectField(_depthNormalMaterial, typeof(Material), false);
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

            EditorGUILayout.LabelField("Copyright (c) 2020, See1Studios.", Styles.centeredMinilabel);
            //GUILayout.Label("© 2020 See1 Studios All right reserved.", EditorStyles.miniLabel);
            //settings.mouseAccelerationEnabled = GUILayout.Toggle(settings.mouseAccelerationEnabled, "Mouse Acceleration Enabled", "Button", GUILayout.Height(32));
            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                settings.reframeToTarget = GUILayout.Toggle(settings.reframeToTarget, "Reframe Target",
            //                    EditorStyles.miniButtonLeft);
            //                settings.recalculateBound = GUILayout.Toggle(settings.recalculateBound, "Recalculate Bound",
            //                    EditorStyles.miniButtonRight);
            //            }

            //            _prefab = EditorGUILayout.ObjectField(_prefab, typeof(GameObject), false) as GameObject;

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                if (GUILayout.Button("Primitives", GUILayout.Width(80), GUILayout.Height(32)))
            //                {
            //                    int number = UnityEngine.Random.Range(1, 4);
            //                    var primitive = GameObject.CreatePrimitive((PrimitiveType)number);
            //                    SetModel(primitive);
            //                    DestroyImmediate(primitive);
            //                }

            //                if (GUILayout.Button("Create", GUILayout.Height(32)))
            //                {
            //                    if (_prefab)
            //                    {
            //                        SetModel(_prefab);
            //                    }
            //                }
            //            }

            //            Styles.Foldout(true, "Control");
            //            settings.rotSpeed = EditorGUILayout.IntSlider("Rotate Speed", settings.rotSpeed, 0, 10);
            //            settings.zoomSpeed = EditorGUILayout.IntSlider("Zoom Speed", settings.zoomSpeed, 0, 10);
            //            settings.panSpeed = EditorGUILayout.IntSlider("Pan Speed", settings.panSpeed, 0, 10);
            //            settings.smoothFactor = EditorGUILayout.IntSlider("Smoothness", settings.smoothFactor, 0, 10);
            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                _autoRotateCamera = GUILayout.Toggle(_autoRotateCamera, "Rotate Camera", EditorStyles.miniButton,
            //                    GUILayout.Width(_labelWidth));
            //                _cameraAutoRotationSpeed = EditorGUILayout.IntSlider(_cameraAutoRotationSpeed, -10, 10);
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                _autoRotateLight = GUILayout.Toggle(_autoRotateLight, "Rotate Light", EditorStyles.miniButton,
            //                    GUILayout.Width(_labelWidth));
            //                _lightAutoRotationSpeed = EditorGUILayout.IntSlider(_lightAutoRotationSpeed, -10, 10);
            //            }

            //            Styles.Foldout(true, "Capture");

            //            GUILayout.Label(
            //                string.Format("Image Size : {0} x {1}", _renderRect.width * settings.captureMultiplier,
            //                    _renderRect.height * settings.captureMultiplier), EditorStyles.miniLabel);
            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                settings.captureMultiplier = EditorGUILayout.IntSlider(settings.captureMultiplier, 1, 8);
            //                settings.screenshotAlpha =
            //                    GUILayout.Toggle(settings.screenshotAlpha, "Alpha", EditorStyles.miniButton);
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (new EditorGUILayout.VerticalScope())
            //                {
            //                    settings.openSavedImage = GUILayout.Toggle(settings.openSavedImage, "Open Saved Image",
            //                        EditorStyles.miniButton);
            //                    if (GUILayout.Button("Open Saved Folder", EditorStyles.miniButton))
            //                    {
            //                        EditorUtility.OpenWithDefaultApp(Directory.GetParent(Application.dataPath).ToString() +
            //                                                         "/Screenshots");
            //                    }
            //                }

            //                GUI.backgroundColor = Color.red;
            //                if (GUILayout.Button("Screenshot", GUILayout.Height(32)))
            //                {
            //                    Texture2D tex = RenderToTexture((int)settings.captureMultiplier, settings.screenshotAlpha);
            //                    string savedPath = SaveAsFile(tex,
            //                        Directory.GetParent(Application.dataPath).ToString() + "/Screenshots", _targetGo.name,
            //                        "MMddHHmmss");
            //                    if (settings.openSavedImage)
            //                    {
            //                        EditorUtility.OpenWithDefaultApp(savedPath);
            //                    }


            //                }

            //                GUI.backgroundColor = Color.white;
            //            }

            //            Styles.Foldout(true, "View");
            //            //_targetOffset = EditorGUILayout.Vector3Field("Target Offset", _targetOffset);
            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                if (GUILayout.Button("Front", EditorStyles.miniButtonLeft))
            //                {
            //                    _destRot = new Vector2(180, 0);
            //                }

            //                if (GUILayout.Button("Left", EditorStyles.miniButtonMid))
            //                {
            //                    _destRot = new Vector2(90, 0);
            //                }

            //                if (GUILayout.Button("Back", EditorStyles.miniButtonMid))
            //                {
            //                    _destRot = Vector2.zero;
            //                }

            //                if (GUILayout.Button("Right", EditorStyles.miniButtonMid))
            //                {
            //                    _destRot = new Vector2(-90, 0);
            //                }

            //                if (GUILayout.Button("Top", EditorStyles.miniButtonMid))
            //                {
            //                    _destRot = new Vector2(180, 90);
            //                }

            //                if (GUILayout.Button("Bottom", EditorStyles.miniButtonRight))
            //                {
            //                    _destRot = new Vector2(180, -90);
            //                }
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                _preview.camera.fieldOfView =
            //                    EditorGUILayout.IntSlider("Field Of View", (int)_preview.camera.fieldOfView, 10, 90);
            //                _preview.camera.orthographic =
            //                    GUILayout.Toggle(_preview.camera.orthographic, _preview.camera.orthographic ? "O" : "P",
            //                        EditorStyles.miniButton, GUILayout.Width(20));
            //            }

            //            if (GUILayout.Button("Add Custom View", EditorStyles.miniButton))
            //            {
            //                settings.viewList.Add(new View(_destRot, _destDistance, _destPivotPos,
            //                    _preview.cameraFieldOfView));
            //            }


            //            using (var scope = new EditorGUILayout.HorizontalScope())
            //            {
            //                float width = 0;
            //                if (Event.current.type == EventType.Repaint)
            //                {
            //                    //Rect left = new Rect(scope.rect.position, new Vector2(scope.rect.size.x * 0.5f, scope.rect.size.y));
            //                    //EditorGUI.DrawRect(left, Color.red);
            //                    //Rect right = new Rect(new Vector2(scope.rect.position.x + left.size.x, scope.rect.position.y),                        left.size);
            //                    //EditorGUI.DrawRect(right, Color.green);
            //                    width = scope.rect.size.x * 0.5f;
            //                }

            //                using (new EditorGUILayout.VerticalScope(GUILayout.Width(110)))
            //                {
            //                    for (var i = 0; i < settings.viewList.Count; i = i + 2)
            //                    {
            //                        using (new EditorGUILayout.HorizontalScope())
            //                        {
            //                            var view = settings.viewList[i];
            //                            if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(30)))
            //                            {
            //                                view.rotation = _destRot;
            //                                view.distance = _destDistance;
            //                                view.pivot = _destPivotPos;
            //                                view.fieldOfView = _preview.cameraFieldOfView;
            //                            }

            //                            if (GUILayout.Button(i.ToString(), EditorStyles.miniButtonMid))
            //                            {
            //                                _destRot = view.rotation;
            //                                _destDistance = view.distance;
            //                                _destPivotPos = view.pivot;
            //                                _preview.cameraFieldOfView = view.fieldOfView;
            //                            }

            //                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
            //                            {
            //                                settings.viewList.Remove(view);
            //                            }
            //                        }
            //                    }
            //                }

            //                using (new EditorGUILayout.VerticalScope())
            //                {
            //                    for (var i = 1; i < settings.viewList.Count; i = i + 2)
            //                    {

            //                        using (new EditorGUILayout.HorizontalScope())
            //                        {
            //                            var view = settings.viewList[i];
            //                            if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(30)))
            //                            {
            //                                view.rotation = _destRot;
            //                                view.distance = _destDistance;
            //                                view.pivot = _destPivotPos;
            //                                view.fieldOfView = _preview.cameraFieldOfView;
            //                            }

            //                            if (GUILayout.Button(i.ToString(), EditorStyles.miniButtonMid))
            //                            {
            //                                _destRot = view.rotation;
            //                                _destDistance = view.distance;
            //                                _destPivotPos = view.pivot;
            //                                _preview.cameraFieldOfView = view.fieldOfView;
            //                            }


            //                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
            //                            {
            //                                settings.viewList.Remove(view);
            //                            }
            //                        }
            //                    }
            //                }
            //            }

            //            GUILayout.Label("Viewport Sizes", EditorStyles.miniLabel);
            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                if (GUILayout.Button("New", EditorStyles.miniButtonLeft))
            //                {
            //                    ShowPopupWindow();
            //                }

            //                if (GUILayout.Button("Add Current", EditorStyles.miniButtonRight))
            //                {
            //                    AddViewportSize(_renderRect.size);
            //                }
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (new EditorGUILayout.VerticalScope())
            //                {
            //                    for (var i = 0; i < settings.viewportSizes.Count; i = i + 2)
            //                    {
            //                        using (new EditorGUILayout.HorizontalScope())
            //                        {
            //                            var size = settings.viewportSizes[i];
            //                            if (GUILayout.Button(string.Format("{0}x{1}", size.x.ToString("#"), size.y.ToString("#")),
            //                                EditorStyles.miniButtonLeft))
            //                            {
            //                                ResizeWindow(size);
            //                            }

            //                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
            //                            {
            //                                settings.viewportSizes.Remove(size);
            //                            }
            //                        }
            //                    }
            //                }

            //                using (new EditorGUILayout.VerticalScope())
            //                {
            //                    for (var i = 1; i < settings.viewportSizes.Count; i = i + 2)
            //                    {
            //                        using (new EditorGUILayout.HorizontalScope())
            //                        {
            //                            var size = settings.viewportSizes[i];
            //                            if (GUILayout.Button(string.Format("{0}x{1}", size.x.ToString("#"), size.y.ToString("#")),
            //                                EditorStyles.miniButtonLeft))
            //                            {
            //                                ResizeWindow(size);
            //                            }

            //                            if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(30)))
            //                            {
            //                                settings.viewportSizes.Remove(size);
            //                            }
            //                        }
            //                    }
            //                }
            //            }

            //            Styles.Foldout(true, "Materials");
            //            using (new EditorGUI.DisabledGroupScope(true))
            //            {
            //                foreach (var mat in _targetInfo.materials)
            //                {
            //                    EditorGUILayout.ObjectField("", mat, typeof(Material), false);
            //                }
            //            }

            //            Styles.Foldout(true, "Environment");

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                EditorGUILayout.PrefixLabel("Background");
            //                using (var check = new EditorGUI.ChangeCheckScope())
            //                {
            //                    bool isSky = (settings.clearFlag == Settings.CleaFlags.Sky);
            //                    isSky = !GUILayout.Toggle(!isSky, "Color", EditorStyles.miniButtonLeft);
            //                    isSky = GUILayout.Toggle(isSky, "Environment", EditorStyles.miniButtonRight);
            //                    if (check.changed)
            //                    {
            //                        settings.clearFlag = isSky ? Settings.CleaFlags.Sky : Settings.CleaFlags.Color;

            //                    }
            //                }
            //            }


            //            if (settings.clearFlag == Settings.CleaFlags.Sky)
            //            {
            //                using (new EditorGUI.DisabledGroupScope(true))
            //                {
            //                    EditorGUILayout.ObjectField("Material", _skyMaterial, typeof(Material), false);
            //                }
            //                _preview.camera.clearFlags = CameraClearFlags.Skybox;
            //            }
            //            else
            //            {
            //                using (new EditorGUILayout.HorizontalScope())
            //                {
            //                    settings.bgColor = EditorGUILayout.ColorField("Color", settings.bgColor);
            //                    _preview.camera.backgroundColor = settings.bgColor;
            //                    _preview.camera.clearFlags = CameraClearFlags.SolidColor;
            //                }
            //            }

            //            _preview.ambientColor = settings.ambientSkyColor =
            //                EditorGUILayout.ColorField("Ambient", settings.ambientSkyColor);
            //            _probe.customBakedTexture = settings.cubeMap = (Cubemap)EditorGUILayout.ObjectField("Environment", settings.cubeMap, typeof(Cubemap), false);

            //            settings.cubeMapmipMapBias = EditorGUILayout.IntSlider("Bias", (int)settings.cubeMapmipMapBias, 0, 10);

            //            //settings.enableSRP = GUILayout.Toggle(settings.enableSRP, "Enable Scriptable Render Pipeline", EditorStyles.miniButton);

            //            Styles.Foldout(true, "Light");

            //            foreach (var previewLight in _preview.lights)
            //            {
            //                using (new EditorGUILayout.HorizontalScope())
            //                {
            //                    using (var lightCheck = new EditorGUI.ChangeCheckScope())
            //                    {
            //                        previewLight.color = EditorGUILayout.ColorField(previewLight.color);
            //                        previewLight.intensity = EditorGUILayout.Slider(previewLight.intensity, 0, 2);
            //                        settings.enableShadows =
            //                            GUILayout.Toggle(settings.enableShadows, "S", EditorStyles.miniButton);
            //                        if (lightCheck.changed)
            //                        {
            //                            previewLight.shadows = settings.enableShadows ? LightShadows.Soft : LightShadows.None;
            //                        }
            //                    }
            //                }
            //            }

            //            Styles.Foldout(true, "Render");

            //            settings.viewportMultiplier = GUILayout.Toggle((settings.viewportMultiplier == 2), "Enable Viewport Supersampling", EditorStyles.miniButton) ? 2 : 1;

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (var cbCheck = new EditorGUI.ChangeCheckScope())
            //                {
            //                    _gridEnabled = GUILayout.Toggle(_gridEnabled, "Grid", EditorStyles.miniButton, GUILayout.Width(_labelWidth));
            //                    //_gridSize = EditorGUILayout.IntSlider(_gridSize, 0, 100);
            //                    if (cbCheck.changed)
            //                    {
            //                        SetGridBuffer(_gridEnabled);
            //                    }
            //                }
            //                _gridColor = EditorGUILayout.ColorField(_gridColor);
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (var cbCheck = new EditorGUI.ChangeCheckScope())
            //                {
            //                    _wireFrameEnabled = GUILayout.Toggle(_wireFrameEnabled, "WireFrame", EditorStyles.miniButton,
            //                        GUILayout.Width(_labelWidth));
            //                    if (cbCheck.changed)
            //                    {
            //                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _wireCommandBuffer, _wireMaterial,
            //                            _wireFrameEnabled);
            //                    }
            //                }

            //                settings.wireLineColor = EditorGUILayout.ColorField(settings.wireLineColor);
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (var cbCheck = new EditorGUI.ChangeCheckScope())
            //                {
            //                    _colorEnabled = GUILayout.Toggle(_colorEnabled, "Color", EditorStyles.miniButton,
            //                        GUILayout.Width(_labelWidth));
            //                    if (cbCheck.changed)
            //                    {
            //                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _colorCommandBuffer, _colorMaterial,
            //                            _colorEnabled);
            //                    }
            //                }

            //                _color = EditorGUILayout.ColorField(_color);
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (var psCheck = new EditorGUI.ChangeCheckScope())
            //                {
            //                    settings.enablePlaneShadows = GUILayout.Toggle(settings.enablePlaneShadows, "PlaneShadow",
            //                        EditorStyles.miniButton, GUILayout.Width(_labelWidth));
            //                    if (psCheck.changed)
            //                    {
            //                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _shadowCommandBuffer, _shadowMaterial,
            //                            settings.enablePlaneShadows);
            //                    }
            //                }

            //                settings.planeShadowColor = EditorGUILayout.ColorField(settings.planeShadowColor);
            //            }

            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                using (var cbCheck = new EditorGUI.ChangeCheckScope())
            //                {
            //                    _DepthNormalEnabled = GUILayout.Toggle(_DepthNormalEnabled, "Normal Visualize",
            //                        EditorStyles.miniButton);
            //                    if (cbCheck.changed)
            //                    {
            //                        _preview.camera.depthTextureMode =
            //                            _DepthNormalEnabled ? DepthTextureMode.DepthNormals : DepthTextureMode.None;
            //                        SetCameraTargetBlitBuffer(CameraEvent.AfterForwardOpaque, _normalCommandBuffer, _normalMaterial, _DepthNormalEnabled);
            //                        //if (_DepthNormalEnabled)
            //                        //{
            //                        //    _preview.camera.SetReplacementShader(_normalMaterial.shader, string.Empty);
            //                        //}
            //                        //else
            //                        //{
            //                        //    _preview.camera.ResetReplacementShader();
            //                        //}
            //                    }
            //                }
            //            }

            //            Styles.Foldout(true, "Post Process");
            //#if UNITY_POST_PROCESSING_STACK_V2

            //            using (var check = new EditorGUI.ChangeCheckScope())
            //            {
            //                settings.enablePostProcess = GUILayout.Toggle(settings.enablePostProcess, "Enable Post Processing",
            //                    EditorStyles.miniButton);
            //                if (settings.enablePostProcess)
            //                    settings.profile =
            //                        (PostProcessProfile)EditorGUILayout.ObjectField("", settings.profile,
            //                            typeof(PostProcessProfile), false);
            //                if (check.changed)
            //                {
            //                    SetPostProcess();
            //                }
            //            }

            //            //if (_ppsEditor) _ppsEditor.OnInspectorGUI();
            //#else
            //                    EditorGUILayout.HelpBox("To use Post Process, add the Post Process Stack V2 package to your project.", MessageType.Info);
            //#endif

            //            Styles.Foldout(true, "Gizmos");
            //            using (new EditorGUILayout.HorizontalScope())
            //            {
            //                string[] enumNames = Enum.GetNames(_gizmoMode.GetType());
            //                bool[] buttons = new bool[enumNames.Length];
            //                using (var check = new EditorGUI.ChangeCheckScope())
            //                {
            //                    _gizmoMode = GUILayout.Toggle((int)_gizmoMode == 0, "None", EditorStyles.miniButtonLeft)
            //                        ? 0
            //                        : _gizmoMode;
            //                    int buttonsValue = 0;
            //                    for (int i = 0; i < buttons.Length; i++)
            //                    {
            //                        buttons[i] = ((int)_gizmoMode & (1 << i + 1)) == (1 << i + 1);
            //                        buttons[i] = GUILayout.Toggle(buttons[i], enumNames[i], EditorStyles.miniButtonMid);
            //                        if (buttons[i])
            //                        {
            //                            buttonsValue += 1 << i + 1;
            //                        }
            //                    }

            //                    if (check.changed)
            //                    {
            //                        _gizmoMode = (GizmoMode)buttonsValue;
            //                    }

            //                    //_gizmoMode = GUILayout.Toggle((int)_gizmoMode == ~0, "All", EditorStyles.miniButtonRight) ? (GizmoMode)~0 : _gizmoMode;
            //                    if (GUILayout.Button("All", EditorStyles.miniButtonRight))
            //                    {
            //                        _gizmoMode = (GizmoMode)~0;
            //                    }
            //                }
            //            }

            GUI.color = Color.white;
            GUI.backgroundColor = Color.white;
        }

        void OnGUI_Info(Rect r)
        {
            if (!_overlayEnabled) return;
            Rect area = new RectOffset(4, 4, 4, 4).Remove(r);
            var style = new GUIStyle(EditorStyles.miniLabel);
            style.alignment = TextAnchor.LowerLeft;
            style.normal.textColor = Color.white;
            _sb0.Append(string.Format("{0}:{1}x{2}", "Viewport : ", _viewPortRect.width.ToString("0"),
                _viewPortRect.height.ToString("0")));
            _sb0.AppendLine();
            _sb0.Append(string.Format("{0}:{1}", "Distance : ", _dist.ToString("0.00")));
            _sb0.AppendLine();
            _sb0.Append(FPS.GetString());
            //_sb0.Append(string.Format("{0}:{1}", "GetObjectPickerControlID : ", EditorGUIUtility.GetObjectPickerControlID().ToString()));
            //_sb0.AppendLine();
            if (EditorGUIUtility.GetObjectPickerControlID()!=0) //picker 가 없을 때는 0
            {
                if (EditorGUIUtility.GetObjectPickerObject() != null)
                {
                    _sb0.AppendLine();
                    _sb0.Append(string.Format("{0}:{1}", "ObjectPicker : ", EditorGUIUtility.GetObjectPickerObject()));
                    //_sb0.AppendLine();
                }
            }
            _sb0.Append("\n");
            _sb0.Append(string.Format("{0}:{1}", "Dest Distance : ", _destDistance.ToString()));
            _sb0.Append("\n");
            _sb0.Append(string.Format("{0}:{1}", "Dest Rotation : ", _destRot.ToString()));
            _sb0.Append("\n");
            _sb0.Append(string.Format("{0}:{1}", "Dest Pivot Position : ", _destPivotPos.ToString()));
            _sb0.Append("\n");
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
            if (_targetGo && (_gizmoMode != 0))
            {

                if (Event.current.type == EventType.Repaint)
                {
                    Rect gizmoRect = (currentData.viewportMultiplier > 1)
                        ? r
                        : new RectOffset((int)(r.x / currentData.viewportMultiplier), 0, 0, 0)
                            .Remove(r); //이유 불명. 이렇게 해야 제 위치에 나옴 ㅜㅠ

                    //Rect gizmoRect = (settings.viewportMultiplier > 1) ? r : _rs.center;
                    //EditorGUI.DrawRect(gizmoRect, Color.red * 0.5f);
                    //Store FOV
                    float fieldOfView = _preview.camera.fieldOfView;
                    var rt = _preview.camera.targetTexture;
                    if (_updateFOV)
                        _preview.camera.fieldOfView =
                            (float)((double)Mathf.Atan(
                                (rt.width > 0 ? Mathf.Max(1f, (float)rt.height / (float)rt.width) : 1f) *
                                Mathf.Tan((float)((double)_preview.camera.fieldOfView * 0.5 *
                                                   (Math.PI / 180.0)))) * 57.2957801818848 * 2.0);
                    //Set Camera
                    Handles.SetCamera(gizmoRect, _preview.camera);
                    DrawWorldAxis();
                    var scale = _targetInfo.bounds.size.magnitude;

                    DrawBasis(_targetGo.transform, scale * 0.1f, true);


                    var length = 0.05f;// _maxDistance;
                    Handles.color = Color.magenta * 1f;
                    Vector3 rotateCenter = _camPivot.position - _targetOffset;
                    Handles.DrawLine(rotateCenter, rotateCenter + Vector3.right * length);
                    Handles.DrawLine(rotateCenter, rotateCenter - Vector3.right * length);
                    Handles.DrawLine(rotateCenter, rotateCenter + Vector3.up * length);
                    Handles.DrawLine(rotateCenter, rotateCenter - Vector3.up * length);
                    Handles.DrawLine(rotateCenter, rotateCenter + Vector3.forward * length);
                    Handles.DrawLine(rotateCenter, rotateCenter - Vector3.forward * length);
                    Handles.Label(rotateCenter, string.Format("View Pivot : {0}\nCam Pivot: {1}\nOffset : {2}", rotateCenter.ToString(), _camPivot.transform.position.ToString(),_targetOffset.ToString()),EditorStyles.miniLabel);

                    //DrawGrid();

                    DrawBasis(_targetGo.transform, scale * 0.1f, true);
                    if ((_gizmoMode & GizmoMode.Info) == GizmoMode.Info)
                    {
                        Handles.color = Color.white;
                        DrawBasis(_targetGo.transform, scale * 0.1f, true);
                        Handles.Label(_targetGo.transform.position, _targetInfo.Print(), EditorStyles.miniLabel);
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
                            if (bone==null) continue;
                            if (bone.parent == null) continue;
                            Handles.color = Color.yellow;
                            //var endpoint = bone.parent.position + bone.parent.rotation * bone.localPosition;
                            Handles.DrawLine(bone.position, bone.parent.position);
                            Handles.color = Color.cyan;
                            Handles.SphereHandleCap(0, bone.position, bone.rotation, 0.01f, EventType.Repaint);
                            DrawBasis(bone, scale * 0.02f, false);
                            //var midPoint = (bone.position + bone.parent.position) / 2;
                            var parentDirection = bone.position + (bone.position - bone.parent.position) * 0.1f;
                            var d =Mathf.Clamp01(1 / _destDistance);
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
                                Handles.color = new Color(color.r, color.g, color.b, 1f);
                                Handles.CircleHandleCap(0, tr.position + tr.forward * -scale,
                                    tr.rotation * Quaternion.LookRotation(Vector3.forward), scale * 0.5f,
                                    EventType.Repaint);
                                Handles.DrawLine(tr.position + tr.forward * -scale, tr.position);
                                Handles.color = new Color(color.r, color.g, color.b, 0.1f);
                                Handles.DrawSolidDisc(tr.position + tr.forward * -scale, tr.forward, scale * 0.5f);
                                Handles.DrawSolidDisc(tr.position + tr.forward * -scale, tr.forward, scale * 0.5f * previewLight.intensity * 0.5f);
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
            Handles.color = Color.gray*0.5f;
            int count = 9;
            int d = count * 2;
            Vector3 offset =new Vector3(-count,0,-count);
            Vector3 startPos = Vector3.zero +offset;
            for (int i = 0; i < d+1; i++)
            {
                Vector3 pos = startPos + new Vector3(i, 0, 0);
                Handles.DrawLine(pos, pos + Vector3.forward * d);
            }
            for (int j = 0; j < d+1; j++)
            {
                Vector3 pos = startPos + new Vector3(0, 0, j);
                Handles.DrawLine(pos, pos + Vector3.right * d);
            }
            Handles.color = color;
        }

        #endregion

        #region Handle Input

        void FitTargetToViewport()
        {
            if (_targetGo)
            {
                Vector3 size = _targetInfo.bounds.max - _targetInfo.bounds.min;
                float largestSize = Mathf.Max(size.x, size.y, size.z);
                float distance = GetFitDistanceOfCamera(_targetInfo.bounds, _preview.camera);
                _destPivotPos = _targetInfo.bounds.center;
                _destDistance = distance;
                _minDistance = distance * 0.1f;
                _maxDistance = largestSize * 10f;
                SetClipPlane();
            }
        }

        float GetFitDistanceOfCamera(Bounds targetBounds, Camera camera)
        {
            float cameraDistance = 1.0f; // 3.0f; // Constant factor
            Vector3 size = targetBounds.max - targetBounds.min;
            float largestSize = Mathf.Max(size.x, size.y, size.z);
            float cameraView =
                2.0f * Mathf.Tan(0.5f * Mathf.Deg2Rad * camera.fieldOfView); // Visible height 1 meter in front
            float distance = cameraDistance * largestSize / cameraView; // Combined wanted distance from the object
            distance += 0.1f * largestSize; // Estimated offset from the center to the outside of the object
            return distance;
        }

        void ProcessInput()
        {
            var axis0 = Vector2.zero;
            var axis1 = Vector2.zero;
            var axis2 = Vector2.zero;
            var zoom = 0.0f;
            var evt = Event.current;
            Rect inputEnabledArea = new Rect(_rs.center.position,new Vector2(_rs.center.width,_rs.center.height-_controlRect.height)); 
            var isLDragging = evt.type == EventType.MouseDrag && evt.button == 0 && _isStartDragValid;
            var isRDragging = evt.type == EventType.MouseDrag && evt.button == 1 && _isStartDragValid;
            var isMDragging = evt.type == EventType.MouseDrag && evt.button == 2 && _isStartDragValid;
            var isScrolling = evt.type == EventType.ScrollWheel && inputEnabledArea.Contains(evt.mousePosition);
            var isLDoubleClicked = evt.isMouse && evt.type == EventType.MouseDown && evt.button == 0 &&
                                   evt.clickCount == 2 && inputEnabledArea.Contains(evt.mousePosition);
            var isRDoubleClicked = evt.isMouse && evt.type == EventType.MouseDown && evt.button == 1 &&
                                   evt.clickCount == 2 && inputEnabledArea.Contains(evt.mousePosition);

            if (evt.type == EventType.MouseDown)
            {
                GUI.FocusControl(null); //Text Field Defocus
                _isStartDragValid = !_rs.right.Contains(evt.mousePosition) &&
                                    inputEnabledArea.Contains(evt.mousePosition);
            }

            if (evt.type == EventType.MouseUp)
            {
                isLDragging = false;
                isRDragging = false;
                isMDragging = false;
                _isStartDragValid = false;
            }

            Vector2 input = evt.delta.normalized;// settings.mouseAccelerationEnabled ? evt.delta * 0.1f : evt.delta.normalized;
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
                switch (evt.keyCode)
                {
                    case KeyCode.BackQuote:
                        _overlayEnabled = !_overlayEnabled;
                        break;
                    case KeyCode.Alpha1:
                        ApplyView(1);
                        break;
                    case KeyCode.Alpha2:
                        ApplyView(2);
                        break;
                    case KeyCode.Alpha3:
                        ApplyView(3);
                        break;
                    case KeyCode.Alpha4:
                        ApplyView(4);
                        break;
                    case KeyCode.Alpha5:
                        ApplyView(5);
                        break;
                    case KeyCode.Alpha6:
                        ApplyView(6);
                        break;
                    case KeyCode.Alpha7:
                        ApplyView(7);
                        break;
                    case KeyCode.Alpha8:
                        ApplyView(8);
                        break;
                    case KeyCode.Alpha9:
                        ApplyView(9);
                        break;
                    case KeyCode.Alpha0:
                        ApplyView(0);
                        break;
                    case KeyCode.F:
                        _destRot = new Vector2(180,0);
                        break;
                    case KeyCode.L:
                        _destRot = new Vector2(90, 0);
                        break;
                    case KeyCode.K:
                        _destRot = Vector2.zero;
                        break;
                    case KeyCode.R:
                        _destRot = new Vector2(-90, 0);
                        break;
                    case KeyCode.T:
                        _destRot = new Vector2(180, 90);
                        break;
                    case KeyCode.B:
                        _destRot = new Vector2(180, -90);
                        break;
                    case KeyCode.G:
                        _gridEnabled = !_gridEnabled;
                        SetGridBuffer(_gridEnabled);
                        break;
                    case KeyCode.P:
                        _preview.camera.orthographic = !_preview.camera.orthographic;
                        break;
                    case KeyCode.F1:
                        RenderAndSaveFile();
                        break;
                    case KeyCode.F3:
                        _wireFrameEnabled = !_wireFrameEnabled;
                        SetModelRenderBuffer(CameraEvent.AfterForwardOpaque, _wireCommandBuffer, _wireMaterial,
                            _wireFrameEnabled);
                        break;
                    case KeyCode.W:
                        _destDistance -= 0.01f;
                        break;
                    case KeyCode.S:
                        _destDistance += 0.01f;
                        break;
                    case KeyCode.A:
                        _destPivotPos += _preview.camera.transform.rotation * new Vector3(-0.01f, 0);
                        break;
                    case KeyCode.D:
                        _destPivotPos += _preview.camera.transform.rotation * new Vector3(0.01f, 0);
                        break;
                    case KeyCode.Escape:
                        _gizmoMode = ~_gizmoMode;
                        break;
                }
                GUIUtility.ExitGUI();
            }
        }

        void UpdateCamera(Vector2 axis0, Vector2 axis2, float wheel)
        {
            float smoothFactor = Mathf.Lerp(10f, 1f, currentData.smoothFactor * 0.2f);

            //ROTATE
            var rotationFactor = axis0;// * Mathf.Pow(currentData.rotSpeed, 2);
            _destRot += rotationFactor;
            _destRot.x = ClampAngle(_destRot.x, -360.0f, 360.0f);
            _destRot.y = ClampAngle(_destRot.y, -90.0f, 90.0f);
            var rotation = _camTr.rotation;
            rotation = Quaternion.Slerp(rotation, Quaternion.Euler(_destRot.y, _destRot.x, 0), _deltaTime * smoothFactor);
            _camTr.rotation = rotation;

            //PAN
            var panFactor = new Vector2(-axis2.x, axis2.y) * (_dist * 0.001f);
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
            for (int i = 0; i < _preview.lights.Length; i++)
            {
                var lightTr = _preview.lights[i].transform;
                lightTr.Rotate(angle, Space.World);
            }
        }

        void SetClipPlane()
        {
            _preview.camera.nearClipPlane = _dist * 0.1f;
            _preview.camera.farClipPlane = _maxDistance * 2;
        }

        void ResetLight()
        {
            _preview.lights[0].transform.rotation = Quaternion.identity;
            _preview.lights[0].color = new Color(0.769f, 0.769f, 0.769f, 0.0f);
            _preview.lights[0].intensity = 1;
            _preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
            _preview.lights[1].color = new Color(0.4f, 0.4f, 0.45f, 0.0f) * 0.7f;
            _preview.lights[1].intensity = 1;

            var angle = new Vector3(0, -180, 0);

            for (int i = 0; i < _preview.lights.Length; i++)
            {
                _preview.lights[i].cullingMask = ~_previewLayer;
                var lightTr = _preview.lights[i].transform;
                lightTr.Rotate(angle);

                _preview.lights[i].shadows = currentData.enableShadows && i == 0 ? LightShadows.Soft : LightShadows.None;
                _preview.lights[i].shadowResolution = LightShadowResolution.VeryHigh;
                _preview.lights[i].shadowBias = 0.01f;
            }
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
            _destRot = view.rotation;
            _destDistance = view.distance;
            _destPivotPos = view.pivot;
            _preview.cameraFieldOfView = view.fieldOfView;
            Notice.Log(string.Format("View {0} Loaded", viewListIndex.ToString()), false);
        }

        #endregion

        #region Utils

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
                string fallBackName = "Unlit/Color";
                shader = Shader.Find(fallBackName);
                Debug.LogWarning(string.Format("{0} Shader not found. Fallback to {1}", shaderName, fallBackName));
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

        static string SaveAsFile(Texture2D texture, string folder, string name, FileExistsMode whenFileExists)
        {
            string addString =(whenFileExists == FileExistsMode.Rename) ? DateTime.Now.ToString("MMddHHmmss"):string.Empty;
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

        private void DrawMesh(Mesh mesh, Material material)
        {
            if (material == null || mesh == null) return;
            Graphics.SetRenderTarget(_preview.camera.targetTexture);
            material.SetPass(0);
            Graphics.DrawMeshNow(mesh, Vector3.zero, Quaternion.identity, 0);
        }

        #endregion

        #region Reflection

        private void GetPreviewLayerID()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var propInfo = typeof(Camera).GetProperty("PreviewCullingLayer", flags);
            _previewLayer = (int)propInfo.GetValue(null, new object[0]);
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
            MethodInfo mi = shaderType.GetMethod("FindBuiltin", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Shader shader = (mi != null) ? mi.Invoke(this, new object[] { shaderName }) as Shader : null;
            return shader;
        }

        #endregion

        [MenuItem("Tools/See1/See1View", false, 0)]
        private static void Init()
        {
            See1View window = EditorWindow.GetWindow<See1View>("See1View");
            window.titleContent =
                new GUIContent("See1View", EditorGUIUtility.IconContent("ViewToolOrbit").image, "See1View");
            window.minSize = new Vector2(128, 128);
            window.Show();
        }
    }
}