using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SimpleRotator : MonoBehaviour
{
    public float rotationSpeed = 45f; // 회전 속도 (초당 45도)

    void Update()
    {
        // 오브젝트를 주어진 속도로 회전시킴
        // 여기에서는 y축을 기준으로 회전하도록 설정함
        transform.Rotate(Vector3.up, rotationSpeed * Time.deltaTime);
    }
}
