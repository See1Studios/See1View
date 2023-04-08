using UnityEditor;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
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
using UnityEngine.Rendering.PostProcessing;
#if UNITY_2019_OR_NEWER
#if UNITY_POST_PROCESSING_STACK_V2
        using UnityEngine.Rendering.PostProcessing;
#endif

#if RENDER_URP
        using UnityEngine.Rendering.Universal;
        using UnityEngine.Profiling;
#endif

#if RENDER_HDRP
        using UnityEngine.Rendering.HighDefinition;
#endif
#if (RENDER_HDRP || RENDER_URP)
#define SRP
#endif

#else

using UnityEngine.Rendering.PostProcessing;

#endif
namespace See1Studios.Editor
{

    public class Test :EditorWindow
    {
        
    }
}