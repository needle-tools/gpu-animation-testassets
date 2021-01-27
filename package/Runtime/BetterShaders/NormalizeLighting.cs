using System;
using UnityEngine;

namespace BetterShaders
{
	[ExecuteAlways]
	public class NormalizeLighting : MonoBehaviour
	{
#if !HDRP_INSTALLED
		private Light light;
		private void Update()
		{
			if (!light && !TryGetComponent(out light))
			{
				enabled = false;
				return;
			}

			if (light.intensity > 100) light.intensity = 1;
		}
#endif
	}
}