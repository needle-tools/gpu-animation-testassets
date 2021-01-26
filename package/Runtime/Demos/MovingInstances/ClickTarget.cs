using UnityEngine;

namespace Demos.MovingInstances
{
    public class ClickTarget : MonoBehaviour
    {
        private void Update()
        {
            if (Input.GetMouseButtonUp(0))
            {
                var cam = Camera.main;
                if (cam)
                {
                    if(Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out var hit))
                    {
                        transform.position = hit.point;
                    }
                }
            }
        }
    }
}
