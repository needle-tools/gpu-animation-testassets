using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace needle.GpuAnimation
{
	public class DynamicInstances : PreviewBakedAnimationBase, IDisposable
	{
		public int ClipIndex = -1;
		public Vector2Int Count = new Vector2Int(10, 10);
		public Vector2 Offset = new Vector2(1, 1);
		public bool UseInstancedIndirect = true;

		private class RenderData : IDisposable
		{
			public ComputeBuffer Buffer;

			public NativeArray<float4x4> Matrices;
			public Matrix4x4[] Matrices_2;

			// args buffer per sub mesh
			public List<ComputeBuffer> Args;

			public void Dispose()
			{
				if (Matrices.IsCreated) Matrices.Dispose();
				Buffer?.Dispose();
				if (Args != null)
					foreach (var ab in Args)
						ab?.Dispose();
			}
		}

		private readonly Dictionary<string, RenderData> buffers = new Dictionary<string, RenderData>();
		private static readonly int PositionBuffer = Shader.PropertyToID("_InstanceTransforms");
		private uint[] args;

		private float[] _timeOffsets;

		[Header("Internal")] public int InstanceCount = default;
		public int VertexCount = 0;
		private static readonly int InstanceTimeOffsets = Shader.PropertyToID("_InstanceTimeOffsets");


		private void OnValidate()
		{
#if UNITY_WEBGL
			if (!UseInstancedIndirect && PreviewMaterial && !PreviewMaterial.enableInstancing)
			{
				Debug.LogWarning("Instancing is disabled, please enable instancing: " + PreviewMaterial, PreviewMaterial);
			}
#endif

#if UNITY_EDITOR
			if (Selection.Contains(this.gameObject)) Dispose();
#endif
		}

		protected override void OnDisable()
		{
			base.OnDisable();
			Dispose();
		}

		public void Dispose()
		{
			foreach (var data in buffers.Values) data.Dispose();
			buffers.Clear();
			// _timeOffsets?.Dispose();
		}

		private bool EnsureBuffers(Object obj, int clipIndex, int clipsCount, out string key)
		{
			var count = Count.x * Count.y;
			InstanceCount = count * clipsCount;

			var offset = Offset;
			if (ClipIndex < 0)
			{
				offset.x *= clipsCount;
			}

			RenderData CreateNewBuffer()
			{
				var buffer = new ComputeBuffer(count, sizeof(float) * 4 * 4);
				var positions = new NativeArray<float4x4>(count, Allocator.Persistent);
				var i = 0;
				for (var x = 0; x < Count.x; x++)
				{
					var ox = x * offset.x;
					if (ClipIndex < 0) ox += clipIndex;
					for (var y = 0; y < Count.y; y++)
					{
						var transform1 = transform;
						positions[i] = float4x4.TRS(new Vector3(ox, 0, y * offset.y) + transform1.position, quaternion.identity, transform1.lossyScale);
						++i;
					}
				}

				buffer.SetData(positions);
				var data = new RenderData();
				data.Buffer = buffer;
				data.Matrices = positions;
				data.Args = new List<ComputeBuffer>();
				return data;
			}

			key = obj.name + clipIndex;

			if (!buffers.ContainsKey(key))
				buffers.Add(key, CreateNewBuffer());
			else if (buffers.ContainsKey(key) && !buffers[key].Buffer.IsValid() || buffers[key].Buffer.count != count)
			{
				buffers[key].Buffer.Dispose();
				buffers[key] = CreateNewBuffer();
			}

			if (_timeOffsets == null)// || !_timeOffsets.IsValid() || _timeOffsets.count != InstanceCount)
			{
				var times = new float[100];
				for (var i = 0; i < times.Length; i++)
				{
					times[i] = Random.value * 100;
				}
				_timeOffsets = times;
				// _timeOffsets = new ComputeBuffer(times.Length, sizeof(float));
				// _timeOffsets.SetData(times);
			}

			return true;
		}

		public Transform Target;
		public float Speed = 1;
		public float Separation = 3;
		public int MaxNeighbors = 50;

		private Unity.Mathematics.Random random = new Unity.Mathematics.Random(100);

		protected override void Render(Camera cam, Mesh mesh, Material material, MaterialPropertyBlock block, int clipIndex, int clipsCount)
		{
			if (ClipIndex >= 0 && ClipIndex != clipIndex) return;

			if (transform.hasChanged)
			{
				transform.hasChanged = false;
				Dispose();
			}

			if (!EnsureBuffers(mesh, clipIndex, clipsCount, out var key)) return;

			VertexCount = mesh.vertexCount * InstanceCount;

			var data = buffers[key];

			if (Target)
			{
				var tp = (float3)Target.position;
				var md = new MovementData() {matrices = data.Matrices};
				md.target = tp;
				md.separationDist = Separation;
				md.maxNeighbors = MaxNeighbors;
				md.speed = Speed;
				md.deltaTime = Time.deltaTime;
				Movement.UpdateMatrices(random, ref md);//, ref tp, MaxNeighbors, Separation, Speed);
			}

			if (UseInstancedIndirect)
			{
				if (args == null) args = new uint[5];
				data.Buffer.SetData(data.Matrices);
				block.SetBuffer(PositionBuffer, data.Buffer);
			}

			block.SetFloatArray(InstanceTimeOffsets, _timeOffsets);

			for (var k = 0; k < mesh.subMeshCount; k++)
			{
				if (UseInstancedIndirect)
				{
					args[0] = mesh.GetIndexCount(k);
					args[1] = (uint) InstanceCount;
					args[2] = mesh.GetIndexStart(k);
					args[3] = mesh.GetBaseVertex(k);
					if (data.Args.Count <= k) data.Args.Add(new ComputeBuffer(5, sizeof(uint), ComputeBufferType.IndirectArguments));
					var argsBuffer = data.Args[k];
					argsBuffer.SetData(args);

					Graphics.DrawMeshInstancedIndirect(mesh, k, material,
						new Bounds(transform.position, Vector3.one * 100000), argsBuffer, 0, block,
						ShadowCastingMode.On, true, 0, cam);
				}
				else
				{
					if (!material.enableInstancing)
					{
						Debug.LogError("Instancing is disabled on assigned material " + this, this);
						material.enableInstancing = true;
					}
					if (data.Matrices_2 == null || data.Matrices_2.Length != data.Matrices.Length)
						data.Matrices_2 = new Matrix4x4[data.Matrices.Length];
					data.Matrices.Reinterpret<Matrix4x4>().CopyTo(data.Matrices_2);
					var mats = data.Matrices_2;
					var count = mats.Length;
					// mats.Reinterpret<Matrix4x4>();
					Graphics.DrawMeshInstanced(mesh, k, material, mats, count, block, ShadowCastingMode.On, false, 0, cam);
				}
			}
		}

		private struct MovementData
		{
			public NativeArray<float4x4> matrices;
			public float3 target;
			public int maxNeighbors;
			public float separationDist;
			public float speed;
			public float deltaTime;
		}

		[BurstCompile]
		private static class Movement
		{
			[BurstCompile]
			public static void UpdateMatrices(Unity.Mathematics.Random random, ref MovementData data)
			{
				var matrices = data.matrices;
				var target = data.target;
				var maxNeighbors = data.maxNeighbors;
				var separationDist = data.separationDist;
				var speed = data.speed;
				var deltaTime = data.deltaTime;

				for (var i = 0; i < matrices.Length; i++)
				{
					float4x4 matrix = matrices[i];
					var position = ((float4) matrix[3]).xyz;
					float dist = math.distance(position, target);
					if (dist < 1) continue;
					var targetDir = math.normalize(target - position);
					targetDir.y = 0;
					var separation = targetDir;
					for (var k = 0; k < maxNeighbors; k++)
					{
						if (k >= matrices.Length) continue;
						var index = random.NextInt(0, matrices.Length);
						if (index == i) continue;
						var otherPos = ((float4) matrices[index][3]).xyz;
						separation += CalcSeparationVec(position, otherPos, separationDist);
					}

					var forward = math.normalize(separation);
					position = math.lerp(position, position + forward, deltaTime * speed);
					var look = quaternion.LookRotation(math.lerp(targetDir, forward, .3f), new float3(0, 1, 0));
					var rotation = quaternion.LookRotation(
						matrix[2].xyz,
						matrix[1].xyz
					);
					look = math.slerp(rotation, look, Time.deltaTime / .2f);
					matrices[i] = float4x4.TRS(position, look, 1);
				}
			}

			private static float3 CalcSeparationVec(float3 self, float3 neighbor, float dist)
			{
				var diff = self - neighbor; 
				var diffLen = math.length(diff);
				var scaler = math.clamp(1.0f - diffLen / dist, 0, 1);
				return diff * (scaler / diffLen);
			}
		}
	}
}