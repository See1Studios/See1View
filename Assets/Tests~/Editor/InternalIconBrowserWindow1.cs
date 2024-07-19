using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

public class ImageEncoderWindow : EditorWindow
{
    [SerializeField]
    private Texture2D _texture;
    private string enc = string.Empty;

    private Texture2D _textureR;
    [MenuItem("Tools/ImageEncoderWindow")]
    private static void OpenWindow()
    {
        var window = GetWindow<ImageEncoderWindow>();
        
        window.minSize = new Vector2(300f, 200f);
        window.titleContent = new GUIContent(EditorGUIUtility.IconContent("FilterByType"));
        window.titleContent.text = "ImageEncoderWindow";
        
        window.Show();
    }


    private void OnGUI()
    {
        _texture = (Texture2D)EditorGUILayout.ObjectField(_texture, typeof(Texture2D), false);
        EditorGUILayout.TextArea(enc);
        if (GUILayout.Button("Convert"))
        {
            byte[] bytes;

            bytes = _texture.EncodeToPNG();

            //using (MemoryStream ms = new MemoryStream())
            //{
            //    BinaryFormatter bf = new BinaryFormatter();
            //    bf.Serialize(ms, _texture);
            //    bytes = ms.ToArray();
            //}

            enc = Convert.ToBase64String(bytes);
        }

        var controlRect = EditorGUILayout.GetControlRect();
        _textureR = (Texture2D)EditorGUILayout.ObjectField(_textureR, typeof(Texture2D), false);
        if (GUILayout.Button("Restore"))
        {

            byte[] bytes = System.Convert.FromBase64String(enc);

            _textureR = new Texture2D(1, 1);
            _textureR.LoadImage(bytes);
        }
    }

}