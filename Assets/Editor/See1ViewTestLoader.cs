using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace See1Studios.See1View
{
    public class TestLoader : CustomLoader
    {
        public TestLoader(See1View view) : base(view)
        {
        }

        public override void OnClickButton()
        {
        }

        public override void OnDisable()
        {
        }

        public override void OnEnable()
        {
        }

        public override void OnGUI()
        {
            if(_view != null)
            {
                GUILayout.Label("Test Loader Initialised");
            }
            if(_view.MainTarget != null)
            {
                GUILayout.Label(_view.MainTarget.name);
            }
            if(GUILayout.Button("Add"))
            {
                _view.AddModel(null);
            }
            if(GUILayout.Button("Remove"))
            {
                _view.RemoveModel(null);
            }
            if(GUILayout.Button("Test"))
            {
                _view.GetMainPlayer().boneModifier.Add("Foot");
            }
        }
    }
}