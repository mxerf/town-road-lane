using System;
using System.Runtime.CompilerServices;
using Colossal.Mathematics;
using Game.Areas;
using Game.Buildings;
using Game.City;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;

// Verbatim copy of the vanilla `Game.Net.SecondaryLaneSystem` from `decomp/Game/Game.Net/SecondaryLaneSystem.cs`,
// renamed and re-namespaced so we can register it instead of vanilla. v2 phase-0: behavioural drop-in —
// no logic changes yet, just enough edits to compile outside Game.dll. Phase-1 will add the per-edge
// `MarkingOverride` check inside `UpdateLanesJob.UpdateLanes`.
//
// Differences from the decomp:
//   - namespace renamed Game.Net → TownRoadLane.
//   - class renamed SecondaryLaneSystem → CustomSecondaryLaneSystem.
//   - [CompilerGenerated] dropped.
//   - using Unity.Entities.Internal removed (mods can't use the InternalCompilerInterface helpers).
//   - OnUpdate refreshed via __TypeHandle.__AssignHandles + direct field reads instead of
//     InternalCompilerInterface.Get*; semantically equivalent.
//   - Disambiguating using-aliases added below: in the original namespace `Game.Net`, bare names like
//     `Node`/`Edge`/`SubLane`/`CarLane` resolved to the local Game.Net.* types. Outside that namespace
//     the names collide with Game.Areas / Game.Pathfind / Game.Prefabs, so we alias bare → Game.Net.*
//     to keep the body untouched.
//
// All Game.Net / Game.Prefabs / Game.Common / Game.Tools / Game.Rendering types touched here are public —
// the portability audit (memory: project-v2-architecture.md) found zero blockers.
using Game;
using Game.Net;
using Node = Game.Net.Node;
using Edge = Game.Net.Edge;
using SubLane = Game.Net.SubLane;
using CarLane = Game.Net.CarLane;
using TrackLane = Game.Net.TrackLane;
using PedestrianLane = Game.Net.PedestrianLane;
using ParkingLane = Game.Net.ParkingLane;
using SecondaryLane = Game.Net.SecondaryLane;
using OutsideConnection = Game.Net.OutsideConnection;
using Elevation = Game.Net.Elevation;

namespace TownRoadLane;

// `partial` is required because Unity.Entities' Roslyn source generator emits a backing partial for any
// SystemBase. (Vanilla doesn't have this in the decomp because the decompiler inlined the generated half.)
public partial class CustomSecondaryLaneSystem : GameSystemBase
{
	private struct LaneKey(Lane lane, Entity prefab) : IEquatable<LaneKey>
	{
		private Lane m_Lane = lane;

		private Entity m_Prefab = prefab;

		public void ReplaceOwner(Entity oldOwner, Entity newOwner)
		{
			m_Lane.m_StartNode.ReplaceOwner(oldOwner, newOwner);
			m_Lane.m_MiddleNode.ReplaceOwner(oldOwner, newOwner);
			m_Lane.m_EndNode.ReplaceOwner(oldOwner, newOwner);
		}

		public bool Equals(LaneKey other)
		{
			if (m_Lane.Equals(other.m_Lane))
			{
				return m_Prefab.Equals(other.m_Prefab);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return m_Lane.GetHashCode();
		}
	}

	private struct LaneBuffer(Allocator allocator)
	{
		public NativeParallelHashMap<LaneKey, Entity> m_OldLanes = new NativeParallelHashMap<LaneKey, Entity>(32, allocator);

		public NativeParallelHashMap<LaneKey, Entity> m_OriginalLanes = new NativeParallelHashMap<LaneKey, Entity>(32, allocator);

		public NativeParallelHashSet<Entity> m_Requirements = default(NativeParallelHashSet<Entity>);

		public NativeList<LaneCorner> m_LaneCorners = new NativeList<LaneCorner>(32, allocator);

		public NativeList<CutRange> m_CutRanges = new NativeList<CutRange>(32, allocator);

		public NativeList<CrossingLane> m_CrossingLanes = new NativeList<CrossingLane>(32, allocator);

		public bool m_RequirementsSearched = false;

		public void Clear()
		{
			m_OldLanes.Clear();
			m_OriginalLanes.Clear();
			if (m_Requirements.IsCreated)
			{
				m_Requirements.Clear();
			}
			m_LaneCorners.Clear();
			m_CutRanges.Clear();
			m_CrossingLanes.Clear();
			m_RequirementsSearched = false;
		}

		public void Dispose()
		{
			m_OldLanes.Dispose();
			m_OriginalLanes.Dispose();
			if (m_Requirements.IsCreated)
			{
				m_Requirements.Dispose();
			}
			m_LaneCorners.Dispose();
			m_CutRanges.Dispose();
			m_CrossingLanes.Dispose();
		}
	}

	private struct LaneCorner
	{
		public float3 m_StartPosition;

		public float3 m_EndPosition;

		public float4 m_Tangents;

		public float2 m_Width;

		public Entity m_Lane;

		public PathNode m_StartNode;

		public PathNode m_EndNode;

		public LaneFlags m_Flags;

		public bool m_Inverted;

		public bool m_Duplicates;

		public bool m_Hidden;
	}

	private struct CutRange : IComparable<CutRange>
	{
		public Bounds1 m_Bounds;

		public uint m_Group;

		public int CompareTo(CutRange other)
		{
			return math.select(0, math.select(-1, 1, m_Bounds.min > other.m_Bounds.min), m_Bounds.min != other.m_Bounds.min);
		}
	}

	private struct CrossingLane
	{
		public Entity m_Prefab;

		public float3 m_StartPos;

		public float2 m_StartTangent;

		public float3 m_EndPos;

		public float2 m_EndTangent;

		public bool m_Optional;

		public bool m_Hidden;
	}

	[BurstCompile]
	private struct UpdateLanesJob : IJobChunk
	{
		[ReadOnly]
		public EntityTypeHandle m_EntityType;

		[ReadOnly]
		public ComponentTypeHandle<Node> m_NodeType;

		[ReadOnly]
		public ComponentTypeHandle<Deleted> m_DeletedType;

		[ReadOnly]
		public ComponentTypeHandle<EdgeGeometry> m_EdgeGeometryType;

		[ReadOnly]
		public ComponentTypeHandle<Composition> m_CompositionType;

		[ReadOnly]
		public ComponentTypeHandle<Temp> m_TempType;

		[ReadOnly]
		public BufferTypeHandle<SubLane> m_SubLaneType;

		[ReadOnly]
		public ComponentLookup<Edge> m_EdgeData;

		[ReadOnly]
		public ComponentLookup<Curve> m_CurveData;

		[ReadOnly]
		public ComponentLookup<Lane> m_LaneData;

		[ReadOnly]
		public ComponentLookup<CarLane> m_CarLaneData;

		[ReadOnly]
		public ComponentLookup<TrackLane> m_TrackLaneData;

		[ReadOnly]
		public ComponentLookup<PedestrianLane> m_PedestrianLaneData;

		[ReadOnly]
		public ComponentLookup<ParkingLane> m_ParkingLaneData;

		[ReadOnly]
		public ComponentLookup<MasterLane> m_MasterLaneData;

		[ReadOnly]
		public ComponentLookup<SlaveLane> m_SlaveLaneData;

		[ReadOnly]
		public ComponentLookup<SecondaryLane> m_SecondaryLaneData;

		[ReadOnly]
		public ComponentLookup<EdgeLane> m_EdgeLaneData;

		[ReadOnly]
		public ComponentLookup<NodeLane> m_NodeLaneData;

		[ReadOnly]
		public ComponentLookup<HangingLane> m_HangingLaneData;

		[ReadOnly]
		public ComponentLookup<Owner> m_OwnerData;

		[ReadOnly]
		public ComponentLookup<LaneGeometry> m_LaneGeometryData;

		[ReadOnly]
		public ComponentLookup<CullingInfo> m_CullingInfoData;

		[ReadOnly]
		public ComponentLookup<Temp> m_TempData;

		[ReadOnly]
		public ComponentLookup<PrefabRef> m_PrefabRefData;

		[ReadOnly]
		public ComponentLookup<NetLaneArchetypeData> m_PrefabLaneArchetypeData;

		[ReadOnly]
		public ComponentLookup<NetLaneData> m_PrefabLaneData;

		[ReadOnly]
		public ComponentLookup<SecondaryLaneData> m_PrefabSecondaryLaneData;

		[ReadOnly]
		public ComponentLookup<NetLaneGeometryData> m_PrefabLaneGeometryData;

		[ReadOnly]
		public ComponentLookup<NetCompositionData> m_PrefabCompositionData;

		[ReadOnly]
		public ComponentLookup<ParkingLaneData> m_PrefabParkingLaneData;

		[ReadOnly]
		public ComponentLookup<UtilityLaneData> m_PrefabUtilityLaneData;

		[ReadOnly]
		public BufferLookup<SubLane> m_SubLanes;

		[ReadOnly]
		public BufferLookup<LaneOverlap> m_LaneOverlaps;

		[ReadOnly]
		public BufferLookup<SecondaryNetLane> m_PrefabSecondaryLanes;

		[ReadOnly]
		public BufferLookup<ObjectRequirementElement> m_LaneRequirements;

		[ReadOnly]
		public Entity m_DefaultTheme;

		[ReadOnly]
		public bool m_LeftHandTraffic;

		[ReadOnly]
		public ComponentTypeSet m_AppliedTypes;

		[ReadOnly]
		public ComponentTypeSet m_DeletedTempTypes;

		[ReadOnly]
		public ComponentTypeSet m_HideLaneTypes;

		public EntityCommandBuffer.ParallelWriter m_CommandBuffer;

		public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			if (chunk.Has(ref m_DeletedType))
			{
				DeleteLanes(chunk, unfilteredChunkIndex);
			}
			else
			{
				UpdateLanes(chunk, unfilteredChunkIndex);
			}
		}

		private void DeleteLanes(ArchetypeChunk chunk, int chunkIndex)
		{
			BufferAccessor<SubLane> bufferAccessor = chunk.GetBufferAccessor(ref m_SubLaneType);
			for (int i = 0; i < bufferAccessor.Length; i++)
			{
				DynamicBuffer<SubLane> dynamicBuffer = bufferAccessor[i];
				for (int j = 0; j < dynamicBuffer.Length; j++)
				{
					Entity subLane = dynamicBuffer[j].m_SubLane;
					if (m_SecondaryLaneData.HasComponent(subLane))
					{
						m_CommandBuffer.AddComponent(chunkIndex, subLane, default(Deleted));
					}
				}
			}
		}

		private void UpdateLanes(ArchetypeChunk chunk, int chunkIndex)
		{
			LaneBuffer laneBuffer = new LaneBuffer(Allocator.Temp);
			NativeArray<Entity> nativeArray = chunk.GetNativeArray(m_EntityType);
			NativeArray<EdgeGeometry> nativeArray2 = chunk.GetNativeArray(ref m_EdgeGeometryType);
			NativeArray<Composition> nativeArray3 = chunk.GetNativeArray(ref m_CompositionType);
			NativeArray<Temp> nativeArray4 = chunk.GetNativeArray(ref m_TempType);
			BufferAccessor<SubLane> bufferAccessor = chunk.GetBufferAccessor(ref m_SubLaneType);
			bool isNode = chunk.Has(ref m_NodeType);
			bool flag = nativeArray4.Length != 0;
			for (int i = 0; i < nativeArray.Length; i++)
			{
				Entity owner = nativeArray[i];
				DynamicBuffer<SubLane> lanes = bufferAccessor[i];
				int laneIndex = 0;
				Temp ownerTemp = default(Temp);
				if (flag)
				{
					ownerTemp = nativeArray4[i];
					if (m_SubLanes.HasBuffer(ownerTemp.m_Original))
					{
						DynamicBuffer<SubLane> lanes2 = m_SubLanes[ownerTemp.m_Original];
						FillOldLaneBuffer(lanes2, laneBuffer.m_OriginalLanes);
					}
				}
				FillOldLaneBuffer(lanes, laneBuffer.m_OldLanes);
				EdgeGeometry edgeGeometry = default(EdgeGeometry);
				Line3 line = default(Line3);
				Line3 line2 = default(Line3);
				float2 float5 = default(float2);
				float2 float6 = default(float2);
				if (nativeArray2.Length != 0)
				{
					edgeGeometry = nativeArray2[i];
					line = new Line3(edgeGeometry.m_Start.m_Right.a, edgeGeometry.m_Start.m_Left.a);
					line2 = new Line3(edgeGeometry.m_End.m_Left.d, edgeGeometry.m_End.m_Right.d);
					float5 = MathUtils.Right(math.normalizesafe(line.b.xz - line.a.xz));
					float6 = MathUtils.Right(math.normalizesafe(line2.b.xz - line2.a.xz));
				}
				NetCompositionData netCompositionData = default(NetCompositionData);
				NetCompositionData netCompositionData2 = default(NetCompositionData);
				if (nativeArray3.Length != 0)
				{
					Composition composition = nativeArray3[i];
					netCompositionData = m_PrefabCompositionData[composition.m_StartNode];
					netCompositionData2 = m_PrefabCompositionData[composition.m_EndNode];
				}
				float slotAngle2;
				for (int j = 0; j < lanes.Length; j++)
				{
					Entity subLane = lanes[j].m_SubLane;
					if (m_MasterLaneData.HasComponent(subLane) || m_SecondaryLaneData.HasComponent(subLane))
					{
						continue;
					}
					Curve curve = m_CurveData[subLane];
					Lane lane = m_LaneData[subLane];
					PrefabRef prefabRef = m_PrefabRefData[subLane];
					NetLaneData netLaneData = m_PrefabLaneData[prefabRef.m_Prefab];
					if ((netLaneData.m_Flags & LaneFlags.Secondary) != 0 || !m_PrefabSecondaryLanes.TryGetBuffer(prefabRef.m_Prefab, out var bufferData) || bufferData.Length == 0)
					{
						continue;
					}
					float2 float7 = math.normalizesafe(MathUtils.StartTangent(curve.m_Bezier).xz);
					float2 float8 = math.normalizesafe(MathUtils.EndTangent(curve.m_Bezier).xz);
					float3 a = curve.m_Bezier.a;
					float3 d = curve.m_Bezier.d;
					float3 d2 = curve.m_Bezier.d;
					float3 a2 = curve.m_Bezier.a;
					float2 width = netLaneData.m_Width;
					if (m_NodeLaneData.TryGetComponent(subLane, out var componentData))
					{
						width += componentData.m_WidthOffset;
					}
					a.xz += MathUtils.Right(float7) * (width.x * 0.5f);
					d.xz += MathUtils.Left(float8) * (width.x * 0.5f);
					d2.xz += MathUtils.Right(float8) * (width.y * 0.5f);
					a2.xz += MathUtils.Left(float7) * (width.y * 0.5f);
					bool flag2 = false;
					bool flag3 = !m_CullingInfoData.HasComponent(subLane) && m_LaneGeometryData.HasComponent(subLane);
					for (int k = 0; k < bufferData.Length; k++)
					{
						flag2 |= (bufferData[k].m_Flags & SecondaryNetLaneFlags.DuplicateSides) != 0;
					}
					laneBuffer.m_LaneCorners.Add(new LaneCorner
					{
						m_StartPosition = a,
						m_EndPosition = d2,
						m_Tangents = new float4(float7, float8),
						m_Lane = subLane,
						m_StartNode = lane.m_StartNode,
						m_EndNode = lane.m_EndNode,
						m_Width = width,
						m_Inverted = false,
						m_Duplicates = flag2,
						m_Hidden = flag3,
						m_Flags = netLaneData.m_Flags
					});
					laneBuffer.m_LaneCorners.Add(new LaneCorner
					{
						m_StartPosition = d,
						m_EndPosition = a2,
						m_Tangents = new float4(float8, float7),
						m_Lane = subLane,
						m_StartNode = lane.m_EndNode,
						m_EndNode = lane.m_StartNode,
						m_Width = width,
						m_Inverted = true,
						m_Duplicates = flag2,
						m_Hidden = flag3,
						m_Flags = netLaneData.m_Flags
					});
					if (!m_EdgeLaneData.TryGetComponent(subLane, out var componentData2))
					{
						continue;
					}
					bool4 x = componentData2.m_EdgeDelta.xxyy == new float4(0f, 1f, 0f, 1f);
					if (!math.any(x))
					{
						continue;
					}
					CarLaneFlags carLaneFlags = ~(CarLaneFlags.Unsafe | CarLaneFlags.UTurnLeft | CarLaneFlags.Invert | CarLaneFlags.SideConnection | CarLaneFlags.TurnLeft | CarLaneFlags.TurnRight | CarLaneFlags.LevelCrossing | CarLaneFlags.Twoway | CarLaneFlags.IsSecured | CarLaneFlags.Runway | CarLaneFlags.Yield | CarLaneFlags.Stop | CarLaneFlags.SecondaryStart | CarLaneFlags.SecondaryEnd | CarLaneFlags.ForbidBicycles | CarLaneFlags.PublicOnly | CarLaneFlags.Highway | CarLaneFlags.UTurnRight | CarLaneFlags.GentleTurnLeft | CarLaneFlags.GentleTurnRight | CarLaneFlags.Forward | CarLaneFlags.Approach | CarLaneFlags.Roundabout | CarLaneFlags.RightLimit | CarLaneFlags.LeftLimit | CarLaneFlags.ForbidPassing | CarLaneFlags.RightOfWay | CarLaneFlags.TrafficLights | CarLaneFlags.ParkingLeft | CarLaneFlags.ParkingRight | CarLaneFlags.Forbidden | CarLaneFlags.AllowEnter);
					if (m_CarLaneData.HasComponent(subLane))
					{
						carLaneFlags = m_CarLaneData[subLane].m_Flags;
					}
					for (int l = 0; l < bufferData.Length; l++)
					{
						SecondaryNetLane secondaryNetLane = bufferData[l];
						if ((secondaryNetLane.m_Flags & SecondaryNetLaneFlags.Crossing) == 0)
						{
							continue;
						}
						bool flag4 = false;
						bool2 x2 = new bool2(math.any(x.xy), math.any(x.zw));
						if ((secondaryNetLane.m_Flags & SecondaryNetLaneFlags.RequireStop) != 0)
						{
							flag4 = flag4 || (carLaneFlags & (CarLaneFlags.LevelCrossing | CarLaneFlags.Stop | CarLaneFlags.TrafficLights)) == 0;
							x2.x = false;
						}
						if ((secondaryNetLane.m_Flags & SecondaryNetLaneFlags.RequireYield) != 0)
						{
							flag4 = flag4 || (carLaneFlags & CarLaneFlags.Yield) == 0;
							x2.x = false;
						}
						if ((secondaryNetLane.m_Flags & SecondaryNetLaneFlags.RequirePavement) != 0)
						{
							x2 &= (x.xz & ((netCompositionData.m_Flags.m_General & CompositionFlags.General.Pavement) != 0)) | (x.yw & ((netCompositionData2.m_Flags.m_General & CompositionFlags.General.Pavement) != 0));
						}
						if (!math.any(x2) || !CheckRequirements(ref laneBuffer, secondaryNetLane.m_Lane))
						{
							continue;
						}
						float3 float9 = 0f;
						if (m_PrefabSecondaryLaneData.TryGetComponent(secondaryNetLane.m_Lane, out var componentData3))
						{
							float9 = componentData3.m_PositionOffset;
							if ((componentData3.m_Flags & SecondaryLaneDataFlags.FitToParkingSpaces) != 0)
							{
								m_ParkingLaneData.TryGetComponent(subLane, out var componentData4);
								FitToParkingLane(subLane, curve, prefabRef, 0f, out var curveBounds, out var blockedMask, out var slotCount, out var slotAngle, out var skipStartEnd);
								curveBounds = new Bounds1(math.max(0f, curveBounds.min), math.min(1f, curveBounds.max));
								if ((secondaryNetLane.m_Flags & SecondaryNetLaneFlags.RequireContinue) != 0)
								{
									bool2 bool5 = new bool2((componentData4.m_Flags & ParkingLaneFlags.StartingLane) != 0, (componentData4.m_Flags & ParkingLaneFlags.EndingLane) != 0);
									float2 float10 = new float2(curveBounds.min - 0.01f, 0.99f - curveBounds.max) * curve.m_Length;
									if (math.abs(slotAngle) > 0.25f)
									{
										float10 -= netLaneData.m_Width * 0.5f / math.tan(slotAngle);
									}
									skipStartEnd |= bool5 & (float10 < 0.2f);
								}
								float num = 1f / (float)math.max(1, slotCount);
								int2 int5 = math.select(new int2(0, slotCount), new int2(1, slotCount - 1), skipStartEnd);
								float newLength = ((!(math.abs(slotAngle) <= 0.25f)) ? (netLaneData.m_Width * 0.5f / math.cos(MathF.PI / 2f - math.abs(slotAngle))) : (netLaneData.m_Width * 0.5f));
								for (int m = int5.x; m <= int5.y; m++)
								{
									if (m == 0)
									{
										if (((int)blockedMask & 1) == 1)
										{
											continue;
										}
									}
									else if (m == slotCount)
									{
										if (((int)(blockedMask >> m - 1) & 1) == 1)
										{
											if ((componentData4.m_Flags & ParkingLaneFlags.EndingLane) != 0)
											{
												continue;
											}
											ulong blockedMask2 = 0uL;
											for (int n = 0; n < lanes.Length; n++)
											{
												SubLane subLane2 = lanes[n];
												if ((subLane2.m_PathMethods & (PathMethod.Parking | PathMethod.BicycleParking)) != 0)
												{
													Curve curve2 = m_CurveData[subLane2.m_SubLane];
													if (!(math.distancesq(curve2.m_Bezier.a, curve.m_Bezier.d) > 0.0001f))
													{
														PrefabRef prefabRef2 = m_PrefabRefData[subLane2.m_SubLane];
														FitToParkingLane(subLane2.m_SubLane, curve2, prefabRef2, 0f, out var _, out blockedMask2, out var _, out slotAngle2, out var _);
														break;
													}
												}
											}
											if (((int)blockedMask2 & 1) == 1)
											{
												continue;
											}
										}
									}
									else if (((int)(blockedMask >> m - 1) & 3) == 3)
									{
										continue;
									}
									float t = math.lerp(curveBounds.min, curveBounds.max, (float)m * num);
									float2 xz = MathUtils.Tangent(curve.m_Bezier, t).xz;
									float2 value = ((!(math.abs(slotAngle) <= 0.25f)) ? MathUtils.RotateRight(xz, slotAngle) : ((slotAngle < 0f) ? MathUtils.Left(xz) : MathUtils.Right(xz)));
									if (MathUtils.TryNormalize(ref value, newLength))
									{
										float3 float11 = MathUtils.Position(curve.m_Bezier, t);
										AddCrossingLane(laneBuffer, secondaryNetLane.m_Lane, float11 - new float3(value.x, 0f, value.y), float11 + new float3(value.x, 0f, value.y), math.normalizesafe(xz), flag4, flag3);
									}
								}
								continue;
							}
						}
						Line3 line3 = line;
						Line3 line4 = line2;
						line3.xz += float5 * float9.z + MathUtils.Right(float5) * float9.x;
						line4.xz += float6 * float9.z + MathUtils.Right(float6) * float9.x;
						line3.y += float9.y;
						line4.y += float9.y;
						if (x2.x)
						{
							Line2 line5 = new Line2(a2.xz, a2.xz + float7);
							Line2 line6 = new Line2(a.xz, a.xz + float7);
							Line3 line7 = (x.x ? line3 : line4);
							if (MathUtils.Intersect(line7.xz, line5, out var t2) && MathUtils.Intersect(line7.xz, line6, out var t3))
							{
								AddCrossingLane(laneBuffer, secondaryNetLane.m_Lane, MathUtils.Position(line7, t2.x), MathUtils.Position(line7, t3.x), float7, flag4, flag3);
							}
						}
						if (x2.y)
						{
							Line2 line8 = new Line2(d2.xz, d2.xz + float8);
							Line2 line9 = new Line2(d.xz, d.xz + float8);
							Line3 line10 = (x.z ? line3 : line4);
							if (MathUtils.Intersect(line10.xz, line8, out var t4) && MathUtils.Intersect(line10.xz, line9, out var t5))
							{
								AddCrossingLane(laneBuffer, secondaryNetLane.m_Lane, MathUtils.Position(line10, t4.x), MathUtils.Position(line10, t5.x), float8, flag4, flag3);
							}
						}
					}
				}
				for (int num2 = 0; num2 < laneBuffer.m_LaneCorners.Length; num2++)
				{
					LaneCorner laneCorner = laneBuffer.m_LaneCorners[num2];
					LaneCorner laneCorner2 = default(LaneCorner);
					float num3 = math.distance(laneCorner.m_StartPosition.xz, laneCorner.m_EndPosition.xz) * 0.5f;
					Line3.Segment line11 = new Line3.Segment(laneCorner.m_StartPosition, laneCorner.m_StartPosition);
					Line3.Segment line12 = new Line3.Segment(laneCorner.m_EndPosition, laneCorner.m_EndPosition);
					line11.a.xz -= laneCorner.m_Tangents.xy * num3;
					line11.b.xz += laneCorner.m_Tangents.xy * num3;
					line12.a.xz -= laneCorner.m_Tangents.zw * num3;
					line12.b.xz += laneCorner.m_Tangents.zw * num3;
					float num4 = float.MaxValue;
					bool flag5 = false;
					bool flag6 = false;
					bool flag7 = false;
					float2 float12 = math.select(laneCorner.m_Width, laneCorner.m_Width.yx, laneCorner.m_Inverted);
					for (int num5 = 0; num5 < laneBuffer.m_LaneCorners.Length; num5++)
					{
						LaneCorner laneCorner3 = laneBuffer.m_LaneCorners[num5];
						if (((laneCorner.m_Flags ^ laneCorner3.m_Flags) & (LaneFlags.Utility | LaneFlags.Underground)) != 0)
						{
							continue;
						}
						bool flag8 = laneCorner.m_StartNode.Equals(laneCorner3.m_EndNode);
						bool flag9 = laneCorner.m_EndNode.Equals(laneCorner3.m_StartNode);
						if ((laneCorner.m_Flags & LaneFlags.Utility) == 0)
						{
							float2 float13 = math.select(laneCorner3.m_Width.yx, laneCorner3.m_Width, laneCorner3.m_Inverted);
							float2 float14 = (float12 + float13) * 0.25f;
							float14 *= float14;
							if ((flag8 ? 0f : MathUtils.DistanceSquared(line11, laneCorner3.m_EndPosition, out slotAngle2)) > float14.x || (flag9 ? 0f : MathUtils.DistanceSquared(line12, laneCorner3.m_StartPosition, out slotAngle2)) > float14.y)
							{
								continue;
							}
						}
						if (laneCorner.m_Lane == laneCorner3.m_Lane)
						{
							continue;
						}
						bool num6 = math.distancesq(laneCorner.m_Tangents, laneCorner3.m_Tangents.zwxy) < 0.01f;
						bool flag10 = math.distancesq(laneCorner.m_Tangents, -laneCorner3.m_Tangents.zwxy) < 0.01f;
						if (num6 || flag10)
						{
							float num7 = math.max(math.distancesq(laneCorner.m_StartPosition, laneCorner3.m_EndPosition), math.distancesq(laneCorner.m_EndPosition, laneCorner3.m_StartPosition));
							if (num7 < num4)
							{
								num4 = num7;
								laneCorner2 = laneCorner3;
								flag5 = flag10;
								flag6 = flag8;
								flag7 = flag9;
							}
						}
					}
					if (laneCorner2.m_Lane != Entity.Null && !laneCorner.m_Duplicates && laneCorner.m_Lane.Index > laneCorner2.m_Lane.Index)
					{
						continue;
					}
					SecondaryNetLaneFlags secondaryNetLaneFlags = (SecondaryNetLaneFlags)0;
					SecondaryNetLaneFlags secondaryNetLaneFlags2 = (SecondaryNetLaneFlags)0;
					SecondaryNetLaneFlags secondaryNetLaneFlags3 = (SecondaryNetLaneFlags)0;
					SecondaryNetLaneFlags secondaryNetLaneFlags4 = (SecondaryNetLaneFlags)0;
					CarLane carLane = default(CarLane);
					CarLane carLane2 = default(CarLane);
					PedestrianLane pedestrianLane = default(PedestrianLane);
					PedestrianLane pedestrianLane2 = default(PedestrianLane);
					if (m_CarLaneData.HasComponent(laneCorner.m_Lane))
					{
						carLane = m_CarLaneData[laneCorner.m_Lane];
					}
					else if (m_TrackLaneData.HasComponent(laneCorner.m_Lane))
					{
						secondaryNetLaneFlags = (((m_TrackLaneData[laneCorner.m_Lane].m_Flags & TrackLaneFlags.Switch) == 0) ? (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireMerge) : (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireContinue));
					}
					if (m_CarLaneData.HasComponent(laneCorner2.m_Lane))
					{
						carLane2 = m_CarLaneData[laneCorner2.m_Lane];
					}
					else if (m_TrackLaneData.HasComponent(laneCorner2.m_Lane))
					{
						secondaryNetLaneFlags2 = (((m_TrackLaneData[laneCorner2.m_Lane].m_Flags & TrackLaneFlags.Switch) == 0) ? (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireMerge) : (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireContinue));
					}
					if (m_PedestrianLaneData.HasComponent(laneCorner.m_Lane))
					{
						pedestrianLane = m_PedestrianLaneData[laneCorner.m_Lane];
					}
					if (m_PedestrianLaneData.HasComponent(laneCorner2.m_Lane))
					{
						pedestrianLane2 = m_PedestrianLaneData[laneCorner2.m_Lane];
					}
					secondaryNetLaneFlags = (((carLane.m_Flags & CarLaneFlags.Unsafe) == 0 && (pedestrianLane.m_Flags & PedestrianLaneFlags.Unsafe) == 0) ? (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireUnsafe) : (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireSafe));
					secondaryNetLaneFlags2 = (((carLane2.m_Flags & CarLaneFlags.Unsafe) == 0 && (pedestrianLane2.m_Flags & PedestrianLaneFlags.Unsafe) == 0) ? (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireUnsafe) : (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireSafe));
					secondaryNetLaneFlags = (((carLane.m_Flags & CarLaneFlags.ForbidPassing) == 0) ? (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireForbidPassing) : (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireAllowPassing));
					secondaryNetLaneFlags2 = (((carLane2.m_Flags & CarLaneFlags.ForbidPassing) == 0) ? (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireForbidPassing) : (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireAllowPassing));
					if ((carLane.m_Flags & (CarLaneFlags.Approach | CarLaneFlags.Roundabout)) == CarLaneFlags.Roundabout)
					{
						secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireNotRoundabout;
						Lane lane2 = m_LaneData[laneCorner.m_Lane];
						if (!lane2.m_StartNode.OwnerEquals(lane2.m_MiddleNode) || !lane2.m_EndNode.OwnerEquals(lane2.m_MiddleNode))
						{
							secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireRoundabout;
						}
					}
					else
					{
						secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireRoundabout;
					}
					if ((carLane2.m_Flags & (CarLaneFlags.Approach | CarLaneFlags.Roundabout)) == CarLaneFlags.Roundabout)
					{
						secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireNotRoundabout;
						Lane lane3 = m_LaneData[laneCorner2.m_Lane];
						if (!lane3.m_StartNode.OwnerEquals(lane3.m_MiddleNode) || !lane3.m_EndNode.OwnerEquals(lane3.m_MiddleNode))
						{
							secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireRoundabout;
						}
					}
					else
					{
						secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireRoundabout;
					}
					if (m_SlaveLaneData.HasComponent(laneCorner.m_Lane))
					{
						SlaveLane slaveLane = m_SlaveLaneData[laneCorner.m_Lane];
						if (lanes.Length > slaveLane.m_MasterIndex && m_CarLaneData.TryGetComponent(lanes[slaveLane.m_MasterIndex].m_SubLane, out var componentData5) && (componentData5.m_Flags & CarLaneFlags.Unsafe) != 0)
						{
							secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireSafeMaster;
						}
						secondaryNetLaneFlags = (((slaveLane.m_Flags & SlaveLaneFlags.MultipleLanes) == 0) ? (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireMultiple) : (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireSingle));
						secondaryNetLaneFlags = (((slaveLane.m_Flags & SlaveLaneFlags.MergingLane) == 0) ? (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireMerge) : (secondaryNetLaneFlags | SecondaryNetLaneFlags.RequireContinue));
					}
					else
					{
						secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireMultiple | SecondaryNetLaneFlags.RequireMerge;
					}
					if (m_SlaveLaneData.HasComponent(laneCorner2.m_Lane))
					{
						SlaveLane slaveLane2 = m_SlaveLaneData[laneCorner2.m_Lane];
						if (lanes.Length > slaveLane2.m_MasterIndex && m_CarLaneData.TryGetComponent(lanes[slaveLane2.m_MasterIndex].m_SubLane, out var componentData6) && (componentData6.m_Flags & CarLaneFlags.Unsafe) != 0)
						{
							secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireSafeMaster;
						}
						secondaryNetLaneFlags2 = (((slaveLane2.m_Flags & SlaveLaneFlags.MultipleLanes) == 0) ? (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireMultiple) : (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireSingle));
						secondaryNetLaneFlags2 = ((((uint)slaveLane2.m_Flags & (uint)(laneCorner2.m_Inverted ? 512 : 1024)) != 0) ? (((secondaryNetLaneFlags & SecondaryNetLaneFlags.RequireContinue) == 0) ? (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireContinue) : (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireMerge)) : (((slaveLane2.m_Flags & SlaveLaneFlags.MergingLane) == 0) ? (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireMerge) : (secondaryNetLaneFlags2 | SecondaryNetLaneFlags.RequireContinue)));
					}
					else
					{
						secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireMultiple | SecondaryNetLaneFlags.RequireMerge;
					}
					bool flag11 = false;
					if (flag6 || flag7)
					{
						int num8 = 0;
						int num9 = 0;
						num8 += math.select(0, 1, (secondaryNetLaneFlags & SecondaryNetLaneFlags.RequireSafe) != 0);
						num8 += math.select(0, 2, (secondaryNetLaneFlags & SecondaryNetLaneFlags.RequireContinue) != 0);
						num9 += math.select(0, 1, (secondaryNetLaneFlags2 & SecondaryNetLaneFlags.RequireSafe) != 0);
						num9 += math.select(0, 2, (secondaryNetLaneFlags2 & SecondaryNetLaneFlags.RequireContinue) != 0);
						if (num8 == 0 && num9 == 0)
						{
							continue;
						}
						flag11 = (num8 > num9) ^ laneCorner.m_Inverted;
					}
					PrefabRef prefabRef3 = m_PrefabRefData[laneCorner.m_Lane];
					DynamicBuffer<SecondaryNetLane> dynamicBuffer = m_PrefabSecondaryLanes[prefabRef3.m_Prefab];
					DynamicBuffer<SecondaryNetLane> dynamicBuffer2 = default(DynamicBuffer<SecondaryNetLane>);
					secondaryNetLaneFlags3 = (SecondaryNetLaneFlags)((int)secondaryNetLaneFlags3 | ((laneCorner.m_Inverted == m_LeftHandTraffic) ? 1 : 2));
					if (laneCorner2.m_Lane != Entity.Null)
					{
						secondaryNetLaneFlags4 = (SecondaryNetLaneFlags)((int)secondaryNetLaneFlags4 | ((laneCorner.m_Inverted != m_LeftHandTraffic) ? 1 : 2));
						secondaryNetLaneFlags |= SecondaryNetLaneFlags.OneSided;
						secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.OneSided;
						if (flag5)
						{
							secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireParallel;
							secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireParallel;
						}
						else
						{
							secondaryNetLaneFlags |= SecondaryNetLaneFlags.RequireOpposite;
							secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.RequireOpposite;
						}
						PrefabRef prefabRef4 = m_PrefabRefData[laneCorner2.m_Lane];
						dynamicBuffer2 = m_PrefabSecondaryLanes[prefabRef4.m_Prefab];
					}
					else
					{
						secondaryNetLaneFlags3 |= SecondaryNetLaneFlags.OneSided;
						secondaryNetLaneFlags2 |= SecondaryNetLaneFlags.Left | SecondaryNetLaneFlags.Right;
					}
					SecondaryNetLaneFlags secondaryNetLaneFlags5 = (secondaryNetLaneFlags3 ^ (SecondaryNetLaneFlags.Left | SecondaryNetLaneFlags.Right)) | SecondaryNetLaneFlags.CanFlipSides;
					SecondaryNetLaneFlags secondaryNetLaneFlags6 = (secondaryNetLaneFlags4 ^ (SecondaryNetLaneFlags.Left | SecondaryNetLaneFlags.Right)) | SecondaryNetLaneFlags.CanFlipSides;
					for (int num10 = 0; num10 < dynamicBuffer.Length; num10++)
					{
						SecondaryNetLane secondaryNetLane2 = dynamicBuffer[num10];
						bool2 bool6 = new bool2((secondaryNetLane2.m_Flags & secondaryNetLaneFlags3) == secondaryNetLaneFlags3, (secondaryNetLane2.m_Flags & secondaryNetLaneFlags5) == secondaryNetLaneFlags5);
						if ((((secondaryNetLane2.m_Flags & secondaryNetLaneFlags) != 0) | !math.any(bool6)) || !CheckRequirements(ref laneBuffer, secondaryNetLane2.m_Lane))
						{
							continue;
						}
						bool flag12 = laneCorner.m_Hidden;
						bool flag13 = !bool6.x;
						bool2 bool7;
						if (laneCorner2.m_Lane != Entity.Null)
						{
							flag12 |= laneCorner2.m_Hidden;
							if (laneCorner.m_Lane.Index > laneCorner2.m_Lane.Index && (secondaryNetLane2.m_Flags & SecondaryNetLaneFlags.DuplicateSides) == 0)
							{
								continue;
							}
							int num11 = 0;
							while (num11 < dynamicBuffer2.Length)
							{
								SecondaryNetLane secondaryNetLane3 = dynamicBuffer2[num11];
								bool7 = new bool2((secondaryNetLane3.m_Flags & secondaryNetLaneFlags4) == secondaryNetLaneFlags4, (secondaryNetLane3.m_Flags & secondaryNetLaneFlags6) == secondaryNetLaneFlags6);
								if (!(((secondaryNetLane3.m_Flags & secondaryNetLaneFlags2) == 0) & math.any(bool6 & bool7) & (secondaryNetLane2.m_Lane == secondaryNetLane3.m_Lane)))
								{
									num11++;
									continue;
								}
								goto IL_1790;
							}
							continue;
						}
						goto IL_17bf;
						IL_17bf:
						flag13 ^= m_LeftHandTraffic;
						if ((secondaryNetLane2.m_Flags & SecondaryNetLaneFlags.DuplicateSides) != 0)
						{
							CreateSecondaryLane(chunkIndex, ref laneIndex, owner, Entity.Null, laneCorner.m_Lane, secondaryNetLane2.m_Lane, lanes, laneBuffer, 0f, laneCorner.m_Width, MathUtils.Left(float5), MathUtils.Left(float6), flag12, isNode, invertLeft: false, !laneCorner.m_Inverted, mergeStart: false, mergeEnd: false, mergeLeft: false, flag, ownerTemp);
						}
						else if (laneCorner.m_Inverted ^ flag13)
						{
							CreateSecondaryLane(chunkIndex, ref laneIndex, owner, laneCorner2.m_Lane, laneCorner.m_Lane, secondaryNetLane2.m_Lane, lanes, laneBuffer, laneCorner2.m_Width, laneCorner.m_Width, MathUtils.Left(float5), MathUtils.Left(float6), flag12, isNode, flag5 ^ flag13, flag13, flag7, flag6, flag11 ^ flag13, flag, ownerTemp);
						}
						else
						{
							CreateSecondaryLane(chunkIndex, ref laneIndex, owner, laneCorner.m_Lane, laneCorner2.m_Lane, secondaryNetLane2.m_Lane, lanes, laneBuffer, laneCorner.m_Width, laneCorner2.m_Width, MathUtils.Left(float5), MathUtils.Left(float6), flag12, isNode, flag13, flag5 ^ flag13, flag6, flag7, flag11 ^ flag13, flag, ownerTemp);
						}
						continue;
						IL_1790:
						flag13 = !(bool6.x & bool7.x);
						goto IL_17bf;
					}
				}
				for (int num12 = 0; num12 < laneBuffer.m_CrossingLanes.Length; num12++)
				{
					CrossingLane crossingLane = laneBuffer.m_CrossingLanes[num12];
					if (!crossingLane.m_Optional)
					{
						Curve curveData = default(Curve);
						curveData.m_Bezier = NetUtils.StraightCurve(crossingLane.m_StartPos, crossingLane.m_EndPos);
						curveData.m_Length = MathUtils.Length(curveData.m_Bezier);
						CreateSecondaryLane(chunkIndex, ref laneIndex, owner, crossingLane.m_Prefab, laneBuffer, curveData, crossingLane.m_StartTangent, crossingLane.m_EndTangent, 0f, crossingLane.m_Hidden, flag, ownerTemp);
					}
				}
				RemoveUnusedOldLanes(chunkIndex, lanes, laneBuffer.m_OldLanes);
				laneBuffer.Clear();
			}
			laneBuffer.Dispose();
		}

		private void FitToParkingLane(Entity lane, Curve curve, PrefabRef prefabRef, float2 sideOffset, out Bounds1 curveBounds, out ulong blockedMask, out int slotCount, out float slotAngle, out bool2 skipStartEnd)
		{
			curveBounds = new Bounds1(0f, 1f);
			blockedMask = 0uL;
			slotCount = 1;
			slotAngle = 0f;
			skipStartEnd = false;
			if (!m_ParkingLaneData.TryGetComponent(lane, out var componentData) || (componentData.m_Flags & ParkingLaneFlags.VirtualLane) != 0)
			{
				return;
			}
			ParkingLaneData prefabParkingLane = m_PrefabParkingLaneData[prefabRef.m_Prefab];
			if (prefabParkingLane.m_SlotInterval != 0f)
			{
				slotCount = NetUtils.GetParkingSlotCount(curve, componentData, prefabParkingLane);
				float parkingSlotInterval = NetUtils.GetParkingSlotInterval(curve, componentData, prefabParkingLane, slotCount);
				slotAngle = prefabParkingLane.m_SlotAngle;
				float4 float5 = 0f;
				float5.y = (float)slotCount * parkingSlotInterval;
				float5.x = 0f - float5.y;
				float5.zw = math.select(0f, sideOffset * math.tan(MathF.PI / 2f - slotAngle), slotAngle > 0.25f);
				float5 /= curve.m_Length;
				DynamicBuffer<LaneOverlap> dynamicBuffer = m_LaneOverlaps[lane];
				float x;
				switch (componentData.m_Flags & (ParkingLaneFlags.StartingLane | ParkingLaneFlags.EndingLane))
				{
				case ParkingLaneFlags.StartingLane:
					x = curve.m_Length - (float)slotCount * parkingSlotInterval;
					curveBounds.min = 1f + float5.x + float5.z;
					curveBounds.max = 1f + float5.w;
					break;
				case ParkingLaneFlags.EndingLane:
					x = 0f;
					curveBounds.min = float5.z;
					curveBounds.max = float5.y + float5.w;
					skipStartEnd.x = true;
					break;
				default:
					x = (curve.m_Length - (float)slotCount * parkingSlotInterval) * 0.5f;
					curveBounds.min = 0.5f + float5.x * 0.5f + float5.z;
					curveBounds.max = 0.5f + float5.y * 0.5f + float5.w;
					break;
				}
				float3 x2 = curve.m_Bezier.a;
				float num = 0f;
				int i = -1;
				float5 = 0f;
				x = math.max(x, 0f);
				float2 float6 = 2f;
				int num2 = 0;
				if (num2 < dynamicBuffer.Length)
				{
					LaneOverlap laneOverlap = dynamicBuffer[num2++];
					float6 = new float2((int)laneOverlap.m_ThisStart, (int)laneOverlap.m_ThisEnd) * 0.003921569f;
				}
				for (int j = 1; j <= 16; j++)
				{
					float num3 = (float)j * 0.0625f;
					float3 float7 = MathUtils.Position(curve.m_Bezier, num3);
					for (num += math.distance(x2, float7); num >= x || (j == 16 && i < slotCount); i++)
					{
						float5.y = math.select(num3, math.lerp(float5.x, num3, x / num), x < num);
						bool flag = false;
						if (float6.x < float5.y)
						{
							flag = true;
							if (float6.y <= float5.y)
							{
								float6 = 2f;
								while (num2 < dynamicBuffer.Length)
								{
									LaneOverlap laneOverlap2 = dynamicBuffer[num2++];
									float2 float8 = new float2((int)laneOverlap2.m_ThisStart, (int)laneOverlap2.m_ThisEnd) * 0.003921569f;
									if (float8.y > float5.y)
									{
										float6 = float8;
										break;
									}
								}
							}
						}
						if (flag && i >= 0 && i < slotCount)
						{
							blockedMask |= (ulong)(1L << i);
						}
						num -= x;
						float5.x = float5.y;
						x = parkingSlotInterval;
					}
					x2 = float7;
				}
			}
			else
			{
				skipStartEnd.x = (componentData.m_Flags & ParkingLaneFlags.StartingLane) == 0;
				skipStartEnd.y = (componentData.m_Flags & ParkingLaneFlags.EndingLane) == 0;
			}
			slotAngle = math.select(slotAngle, 0f - slotAngle, (componentData.m_Flags & ParkingLaneFlags.ParkingLeft) != 0);
		}

		private void FillOldLaneBuffer(DynamicBuffer<SubLane> lanes, NativeParallelHashMap<LaneKey, Entity> laneBuffer)
		{
			for (int i = 0; i < lanes.Length; i++)
			{
				Entity subLane = lanes[i].m_SubLane;
				if (m_SecondaryLaneData.HasComponent(subLane))
				{
					LaneKey key = new LaneKey(m_LaneData[subLane], m_PrefabRefData[subLane].m_Prefab);
					laneBuffer.TryAdd(key, subLane);
				}
			}
		}

		private void RemoveUnusedOldLanes(int jobIndex, DynamicBuffer<SubLane> lanes, NativeParallelHashMap<LaneKey, Entity> laneBuffer)
		{
			for (int i = 0; i < lanes.Length; i++)
			{
				Entity subLane = lanes[i].m_SubLane;
				if (m_SecondaryLaneData.HasComponent(subLane))
				{
					LaneKey key = new LaneKey(m_LaneData[subLane], m_PrefabRefData[subLane].m_Prefab);
					if (laneBuffer.TryGetValue(key, out var _))
					{
						m_CommandBuffer.RemoveComponent(jobIndex, subLane, in m_AppliedTypes);
						m_CommandBuffer.AddComponent(jobIndex, subLane, default(Deleted));
						laneBuffer.Remove(key);
					}
				}
			}
		}

		private void AddCrossingLane(LaneBuffer laneBuffer, Entity prefab, float3 startPos, float3 endPos, float2 tangent, bool isOptional, bool isHidden)
		{
			float2 startTangent = tangent;
			float2 endTangent = tangent;
			bool flag = true;
			while (flag)
			{
				flag = false;
				for (int i = 0; i < laneBuffer.m_CrossingLanes.Length; i++)
				{
					CrossingLane crossingLane = laneBuffer.m_CrossingLanes[i];
					if (!(crossingLane.m_Prefab != prefab))
					{
						if (math.distancesq(crossingLane.m_EndPos, startPos) < 1f)
						{
							startPos = crossingLane.m_StartPos;
							startTangent = crossingLane.m_StartTangent;
							isOptional &= crossingLane.m_Optional;
							laneBuffer.m_CrossingLanes.RemoveAtSwapBack(i);
							flag = true;
							break;
						}
						if (math.distancesq(crossingLane.m_StartPos, endPos) < 1f)
						{
							endPos = crossingLane.m_EndPos;
							endTangent = crossingLane.m_EndTangent;
							isOptional &= crossingLane.m_Optional;
							laneBuffer.m_CrossingLanes.RemoveAtSwapBack(i);
							flag = true;
							break;
						}
					}
				}
			}
			ref NativeList<CrossingLane> reference = ref laneBuffer.m_CrossingLanes;
			CrossingLane value = new CrossingLane
			{
				m_Prefab = prefab,
				m_StartPos = startPos,
				m_StartTangent = startTangent,
				m_EndPos = endPos,
				m_EndTangent = endTangent,
				m_Optional = isOptional,
				m_Hidden = isHidden
			};
			reference.Add(in value);
		}

		private void CreateSecondaryLane(int jobIndex, ref int laneIndex, Entity owner, Entity leftLane, Entity rightLane, Entity prefab, DynamicBuffer<SubLane> lanes, LaneBuffer laneBuffer, float2 leftWidth, float2 rightWidth, float2 startTangent, float2 endTangent, bool isHidden, bool isNode, bool invertLeft, bool invertRight, bool mergeStart, bool mergeEnd, bool mergeLeft, bool isTemp, Temp ownerTemp)
		{
			SecondaryLaneData secondaryLaneData = m_PrefabSecondaryLaneData[prefab];
			NetLaneGeometryData netLaneGeometryData = default(NetLaneGeometryData);
			if (m_PrefabLaneGeometryData.HasComponent(prefab))
			{
				netLaneGeometryData = m_PrefabLaneGeometryData[prefab];
			}
			Bezier4x3 curve = default(Bezier4x3);
			float num = math.max(0.01f, netLaneGeometryData.m_Size.x);
			laneBuffer.m_CutRanges.Clear();
			Curve curve2 = default(Curve);
			Curve curve3 = default(Curve);
			float slotAngle;
			bool2 skipStartEnd;
			if (leftLane != Entity.Null && rightLane != Entity.Null)
			{
				curve2 = m_CurveData[leftLane];
				curve3 = m_CurveData[rightLane];
				if ((secondaryLaneData.m_Flags & SecondaryLaneDataFlags.FitToParkingSpaces) != 0)
				{
					FitToParkingLane(leftLane, curve2, m_PrefabRefData[leftLane], leftWidth * 0.5f, out var curveBounds, out var blockedMask, out var slotCount, out slotAngle, out skipStartEnd);
					FitToParkingLane(rightLane, curve3, m_PrefabRefData[rightLane], rightWidth * 0.5f, out var curveBounds2, out var blockedMask2, out var slotCount2, out slotAngle, out skipStartEnd);
					GetCutRanges(leftLane, lanes, laneBuffer, curveBounds, blockedMask, slotCount, invertLeft);
					GetCutRanges(rightLane, lanes, laneBuffer, curveBounds2, blockedMask2, slotCount2, invertRight);
				}
				if (invertLeft)
				{
					curve2.m_Bezier = MathUtils.Invert(curve2.m_Bezier);
					leftWidth = leftWidth.yx;
				}
				if (invertRight)
				{
					curve3.m_Bezier = MathUtils.Invert(curve3.m_Bezier);
					rightWidth = rightWidth.yx;
				}
				float2 float5 = math.select(leftWidth / (leftWidth + rightWidth), 0.5f, (leftWidth == 0f) & (rightWidth == 0f));
				curve = MathUtils.Lerp(t: new Bezier4x1(float5.x, float5.x, float5.y, float5.y), curve1: curve2.m_Bezier, curve2: curve3.m_Bezier);
				if (mergeStart || mergeEnd)
				{
					Bezier4x3 curve4 = ((!mergeLeft) ? NetUtils.OffsetCurveLeftSmooth(curve2.m_Bezier, leftWidth * -0.5f - secondaryLaneData.m_CutOffset) : NetUtils.OffsetCurveLeftSmooth(curve3.m_Bezier, rightWidth * 0.5f - secondaryLaneData.m_CutOffset));
					if (!ValidateCurve(curve4))
					{
						return;
					}
					if (mergeStart)
					{
						curve.a = curve4.a;
						curve.b = curve4.b;
					}
					if (mergeEnd)
					{
						curve.c = curve4.c;
						curve.d = curve4.d;
					}
				}
				GetCutRanges(leftLane, secondaryLaneData.m_Flags, laneBuffer, curve, leftWidth, secondaryLaneData.m_CutOverlap, invertLeft, isRight: false, rightLane);
				GetCutRanges(rightLane, secondaryLaneData.m_Flags, laneBuffer, curve, rightWidth, secondaryLaneData.m_CutOverlap, invertRight, isRight: true, leftLane);
				num = math.min(num, math.min(curve2.m_Length, curve3.m_Length) * 0.5f);
			}
			else if (leftLane != Entity.Null)
			{
				curve2 = m_CurveData[leftLane];
				if ((secondaryLaneData.m_Flags & SecondaryLaneDataFlags.FitToParkingSpaces) != 0)
				{
					FitToParkingLane(leftLane, curve2, m_PrefabRefData[leftLane], leftWidth * 0.5f, out var curveBounds3, out var blockedMask3, out var slotCount3, out slotAngle, out skipStartEnd);
					GetCutRanges(leftLane, lanes, laneBuffer, curveBounds3, blockedMask3, slotCount3, invertLeft);
				}
				if (invertLeft)
				{
					curve2.m_Bezier = MathUtils.Invert(curve2.m_Bezier);
					leftWidth = leftWidth.yx;
				}
				curve = NetUtils.OffsetCurveLeftSmooth(curve2.m_Bezier, leftWidth * -0.5f - secondaryLaneData.m_CutOffset);
				if (!ValidateCurve(curve))
				{
					return;
				}
				GetCutRanges(leftLane, secondaryLaneData.m_Flags, laneBuffer, curve, leftWidth, secondaryLaneData.m_CutOverlap, invertLeft, isRight: false, Entity.Null);
				num = math.min(num, curve2.m_Length * 0.5f);
			}
			else if (rightLane != Entity.Null)
			{
				curve3 = m_CurveData[rightLane];
				if ((secondaryLaneData.m_Flags & SecondaryLaneDataFlags.FitToParkingSpaces) != 0)
				{
					FitToParkingLane(rightLane, curve3, m_PrefabRefData[rightLane], rightWidth * 0.5f, out var curveBounds4, out var blockedMask4, out var slotCount4, out slotAngle, out skipStartEnd);
					GetCutRanges(rightLane, lanes, laneBuffer, curveBounds4, blockedMask4, slotCount4, invertRight);
				}
				if (invertRight)
				{
					curve3.m_Bezier = MathUtils.Invert(curve3.m_Bezier);
					rightWidth = rightWidth.yx;
				}
				curve = NetUtils.OffsetCurveLeftSmooth(curve3.m_Bezier, rightWidth * 0.5f - secondaryLaneData.m_CutOffset);
				if (!ValidateCurve(curve))
				{
					return;
				}
				GetCutRanges(rightLane, secondaryLaneData.m_Flags, laneBuffer, curve, rightWidth, secondaryLaneData.m_CutOverlap, invertRight, isRight: true, Entity.Null);
				num = math.min(num, curve3.m_Length * 0.5f);
			}
			if (secondaryLaneData.m_PositionOffset.x != secondaryLaneData.m_CutOffset)
			{
				curve = NetUtils.OffsetCurveLeftSmooth(curve, secondaryLaneData.m_CutOffset - secondaryLaneData.m_PositionOffset.x);
			}
			if (secondaryLaneData.m_PositionOffset.y != 0f)
			{
				curve.a.y += secondaryLaneData.m_PositionOffset.y;
				curve.b.y += secondaryLaneData.m_PositionOffset.y;
				curve.c.y += secondaryLaneData.m_PositionOffset.y;
				curve.d.y += secondaryLaneData.m_PositionOffset.y;
			}
			Bounds1 bounds = new Bounds1(0f, 1f);
			Bounds1 bounds2 = new Bounds1(0f, 0f);
			if (laneBuffer.m_CutRanges.Length >= 2)
			{
				laneBuffer.m_CutRanges.Sort();
			}
			Bezier4x2 curve5 = default(Bezier4x2);
			if (m_PrefabRefData.TryGetComponent(leftLane, out var componentData) && m_PrefabUtilityLaneData.TryGetComponent(componentData, out var componentData2) && componentData2.m_Hanging != 0f)
			{
				m_HangingLaneData.TryGetComponent(leftLane, out var componentData3);
				curve5.a.x = componentData3.m_Distances.x;
				curve5.b.x = (componentData3.m_Distances.x + componentData2.m_Hanging * curve2.m_Length) * (2f / 3f);
				curve5.c.x = (componentData3.m_Distances.y + componentData2.m_Hanging * curve2.m_Length) * (2f / 3f);
				curve5.d.x = componentData3.m_Distances.y;
			}
			if (m_PrefabRefData.TryGetComponent(rightLane, out var componentData4) && m_PrefabUtilityLaneData.TryGetComponent(componentData4, out var componentData5) && componentData5.m_Hanging != 0f)
			{
				m_HangingLaneData.TryGetComponent(rightLane, out var componentData6);
				curve5.a.y = componentData6.m_Distances.x;
				curve5.b.y = (componentData6.m_Distances.x + componentData5.m_Hanging * curve3.m_Length) * (2f / 3f);
				curve5.c.y = (componentData6.m_Distances.y + componentData5.m_Hanging * curve3.m_Length) * (2f / 3f);
				curve5.d.y = componentData6.m_Distances.y;
			}
			for (int i = 0; i < laneBuffer.m_CutRanges.Length; i++)
			{
				CutRange cutRange = laneBuffer.m_CutRanges[i];
				if (cutRange.m_Bounds.min > bounds.min)
				{
					Bounds1 bounds3 = new Bounds1(bounds.min, math.min(bounds.max, cutRange.m_Bounds.min));
					if (bounds3.max > bounds3.min && secondaryLaneData.m_CutMargin > 0.001f)
					{
						if (bounds3.min > 0.001f)
						{
							Bounds1 t = bounds3;
							MathUtils.ClampLength(curve, ref t, secondaryLaneData.m_CutMargin);
							bounds3.min = t.max;
						}
						if (bounds3.max < 0.999f)
						{
							Bounds1 t2 = bounds3;
							MathUtils.ClampLengthInverse(curve, ref t2, secondaryLaneData.m_CutMargin);
							bounds3.max = t2.min;
						}
					}
					if (bounds3.max > bounds3.min)
					{
						Curve curve6 = default(Curve);
						curve6.m_Bezier = MathUtils.Cut(curve, bounds3);
						curve6.m_Length = MathUtils.Length(curve6.m_Bezier);
						if (curve6.m_Length >= num)
						{
							if ((secondaryLaneData.m_Flags & SecondaryLaneDataFlags.InvertOverlapCuts) != 0)
							{
								bounds2.min = bounds3.min;
								bounds3.min = bounds2.max;
								bounds2.max = bounds3.max;
								bounds3.max = bounds2.min;
								curve6.m_Bezier = MathUtils.Cut(curve, bounds3);
								curve6.m_Length = MathUtils.Length(curve6.m_Bezier);
							}
							if (curve6.m_Length >= num)
							{
								if (secondaryLaneData.m_Spacing > 0.1f)
								{
									CalculateSpacing(secondaryLaneData, curve6, out var count, out var offset, out var factor);
									for (int j = 0; j < count; j++)
									{
										float t3 = math.lerp(bounds3.min, bounds3.max, ((float)j + offset) * factor);
										float2 hangingDistances = MathUtils.Position(curve5, t3);
										curve6.m_Bezier = NetUtils.StraightCurve(MathUtils.Position(curve2.m_Bezier, t3), MathUtils.Position(curve3.m_Bezier, t3));
										curve6.m_Length = math.distance(curve6.m_Bezier.a, curve6.m_Bezier.d);
										CreateSecondaryLane(jobIndex, ref laneIndex, owner, prefab, laneBuffer, curve6, startTangent, endTangent, hangingDistances, isHidden, isTemp, ownerTemp);
									}
								}
								else
								{
									float2 hangingDistances2 = math.lerp(new float2(curve5.a.x, curve5.d.x), new float2(curve5.a.y, curve5.d.y), 0.5f);
									CreateSecondaryLane(jobIndex, ref laneIndex, owner, prefab, laneBuffer, curve6, startTangent, endTangent, hangingDistances2, isHidden, isTemp, ownerTemp);
								}
							}
						}
					}
				}
				bounds.min = math.max(bounds.min, cutRange.m_Bounds.max);
				if (bounds.min >= bounds.max)
				{
					break;
				}
			}
			if (!(bounds.max > bounds.min) && (secondaryLaneData.m_Flags & SecondaryLaneDataFlags.InvertOverlapCuts) == 0)
			{
				return;
			}
			Curve curve7 = default(Curve);
			if (bounds.max > bounds.min)
			{
				if (secondaryLaneData.m_CutMargin > 0.001f)
				{
					if (bounds.min > 0.001f)
					{
						Bounds1 t4 = bounds;
						MathUtils.ClampLength(curve, ref t4, secondaryLaneData.m_CutMargin);
						bounds.min = t4.max;
					}
					if (bounds.max < 0.999f)
					{
						Bounds1 t5 = bounds;
						MathUtils.ClampLengthInverse(curve, ref t5, secondaryLaneData.m_CutMargin);
						bounds.max = t5.min;
					}
				}
				curve7.m_Bezier = MathUtils.Cut(curve, bounds);
				curve7.m_Length = MathUtils.Length(curve7.m_Bezier);
			}
			if ((secondaryLaneData.m_Flags & SecondaryLaneDataFlags.InvertOverlapCuts) != 0)
			{
				if (curve7.m_Length < num)
				{
					bounds.min = 1f;
				}
				bounds2.min = bounds.min;
				bounds.min = bounds2.max;
				bounds2.max = bounds.max;
				bounds.max = bounds2.min;
				curve7.m_Bezier = MathUtils.Cut(curve, bounds);
				curve7.m_Length = MathUtils.Length(curve7.m_Bezier);
			}
			if (!(curve7.m_Length >= num))
			{
				return;
			}
			if (secondaryLaneData.m_Spacing > 0.1f)
			{
				CalculateSpacing(secondaryLaneData, curve7, out var count2, out var offset2, out var factor2);
				for (int k = 0; k < count2; k++)
				{
					float t6 = math.lerp(bounds.min, bounds.max, ((float)k + offset2) * factor2);
					float2 hangingDistances3 = MathUtils.Position(curve5, t6);
					curve7.m_Bezier = NetUtils.StraightCurve(MathUtils.Position(curve2.m_Bezier, t6), MathUtils.Position(curve3.m_Bezier, t6));
					curve7.m_Length = math.distance(curve7.m_Bezier.a, curve7.m_Bezier.d);
					CreateSecondaryLane(jobIndex, ref laneIndex, owner, prefab, laneBuffer, curve7, startTangent, endTangent, hangingDistances3, isHidden, isTemp, ownerTemp);
				}
			}
			else
			{
				float2 hangingDistances4 = math.lerp(new float2(curve5.a.x, curve5.d.x), new float2(curve5.a.y, curve5.d.y), 0.5f);
				CreateSecondaryLane(jobIndex, ref laneIndex, owner, prefab, laneBuffer, curve7, startTangent, endTangent, hangingDistances4, isHidden, isTemp, ownerTemp);
			}
		}

		private void CalculateSpacing(SecondaryLaneData secondaryLaneData, Curve curve, out int count, out float offset, out float factor)
		{
			count = Mathf.RoundToInt(curve.m_Length / secondaryLaneData.m_Spacing);
			factor = 1f / (float)count;
			if ((secondaryLaneData.m_Flags & SecondaryLaneDataFlags.EvenSpacing) != 0)
			{
				count--;
				offset = 1f;
			}
			else
			{
				offset = 0.5f;
			}
		}

		private bool ValidateCurve(Bezier4x3 curve)
		{
			float2 value = MathUtils.StartTangent(curve).xz;
			float2 value2 = MathUtils.EndTangent(curve).xz;
			float2 value3 = curve.d.xz - curve.a.xz;
			if (MathUtils.TryNormalize(ref value) && MathUtils.TryNormalize(ref value2) && MathUtils.TryNormalize(ref value3))
			{
				float2 x = new float2(math.dot(value, value3), math.dot(value2, value3));
				if (!(math.dot(value, value2) >= -0.99f))
				{
					return math.cmax(math.abs(x)) <= 0.99f;
				}
				return true;
			}
			return false;
		}

		private void CreateSecondaryLane(int jobIndex, ref int laneIndex, Entity owner, Entity prefab, LaneBuffer laneBuffer, Curve curveData, float2 startTangent, float2 endTangent, float2 hangingDistances, bool isHidden, bool isTemp, Temp ownerTemp)
		{
			PrefabRef component = new PrefabRef(prefab);
			SecondaryLaneData secondaryLaneData = m_PrefabSecondaryLaneData[prefab];
			float2 float5 = secondaryLaneData.m_LengthOffset.x;
			if (secondaryLaneData.m_LengthOffset.y != 0f)
			{
				float2 y = math.normalizesafe(MathUtils.StartTangent(curveData.m_Bezier).xz);
				float2 y2 = math.normalizesafe(MathUtils.EndTangent(curveData.m_Bezier).xz);
				float2 x = new float2(math.dot(startTangent, y), math.dot(endTangent, y2));
				x = math.tan(MathF.PI / 2f - math.acos(math.saturate(math.abs(x))));
				float5 += x * secondaryLaneData.m_LengthOffset.y * 0.5f;
			}
			if (math.any(float5 < 0f))
			{
				float2 x2 = math.clamp(-float5 / math.max(0.001f, curveData.m_Length), 0f, 0.5f);
				curveData.m_Length -= curveData.m_Length * math.csum(x2);
				curveData.m_Bezier = MathUtils.Cut(curveData.m_Bezier, new float2(x2.x, 1f - x2.y));
			}
			Owner component2 = new Owner
			{
				m_Owner = owner
			};
			Elevation component3 = default(Elevation);
			Lane lane = new Lane
			{
				m_StartNode = new PathNode(new PathNode(owner, (ushort)laneIndex++), secondaryNode: true),
				m_MiddleNode = new PathNode(new PathNode(owner, (ushort)laneIndex++), secondaryNode: true),
				m_EndNode = new PathNode(new PathNode(owner, (ushort)laneIndex++), secondaryNode: true)
			};
			Temp temp = default(Temp);
			if (isTemp)
			{
				temp.m_Flags = ownerTemp.m_Flags & (TempFlags.Create | TempFlags.Delete | TempFlags.Select | TempFlags.Modify | TempFlags.Hidden);
				if ((ownerTemp.m_Flags & TempFlags.Replace) != 0)
				{
					temp.m_Flags |= TempFlags.Modify;
				}
			}
			LaneKey laneKey = new LaneKey(lane, component.m_Prefab);
			LaneKey laneKey2 = laneKey;
			if (isTemp)
			{
				ReplaceTempOwner(ref laneKey2, owner);
				GetOriginalLane(laneBuffer, laneKey2, ref temp);
			}
			HangingLane component4 = default(HangingLane);
			bool flag = false;
			if (m_PrefabUtilityLaneData.TryGetComponent(prefab, out var componentData) && componentData.m_Hanging != 0f)
			{
				component4.m_Distances = hangingDistances;
				flag = true;
			}
			if (laneBuffer.m_OldLanes.TryGetValue(laneKey, out var item))
			{
				laneBuffer.m_OldLanes.Remove(laneKey);
				m_CommandBuffer.SetComponent(jobIndex, item, curveData);
				if (flag)
				{
					m_CommandBuffer.AddComponent(jobIndex, item, component4);
				}
				if (isTemp)
				{
					m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
					m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
					m_CommandBuffer.SetComponent(jobIndex, item, temp);
				}
				else if (m_TempData.HasComponent(item))
				{
					m_CommandBuffer.RemoveComponent(jobIndex, item, in m_DeletedTempTypes);
					m_CommandBuffer.AddComponent(jobIndex, item, in m_AppliedTypes);
				}
				else
				{
					m_CommandBuffer.RemoveComponent<Deleted>(jobIndex, item);
					m_CommandBuffer.AddComponent(jobIndex, item, default(Updated));
				}
				return;
			}
			NetLaneArchetypeData netLaneArchetypeData = m_PrefabLaneArchetypeData[component.m_Prefab];
			Entity e = m_CommandBuffer.CreateEntity(jobIndex, netLaneArchetypeData.m_LaneArchetype);
			if (isHidden)
			{
				m_CommandBuffer.RemoveComponent(jobIndex, e, in m_HideLaneTypes);
			}
			m_CommandBuffer.SetComponent(jobIndex, e, component);
			m_CommandBuffer.SetComponent(jobIndex, e, lane);
			m_CommandBuffer.SetComponent(jobIndex, e, curveData);
			m_CommandBuffer.AddComponent(jobIndex, e, component2);
			m_CommandBuffer.AddComponent(jobIndex, e, component3);
			if (flag)
			{
				m_CommandBuffer.SetComponent(jobIndex, e, component4);
			}
			if (isTemp)
			{
				m_CommandBuffer.AddComponent(jobIndex, e, temp);
			}
		}

		private void GetCutRanges(Entity lane, DynamicBuffer<SubLane> lanes, LaneBuffer laneBuffer, Bounds1 bounds, ulong blockedMask, int slotCount, bool invert)
		{
			Bounds1 bounds2 = new Bounds1(0f, 1f);
			if (bounds.min > 0.0001f && m_ParkingLaneData.TryGetComponent(lane, out var componentData) && (componentData.m_Flags & ParkingLaneFlags.StartingLane) == 0)
			{
				Curve curve = m_CurveData[lane];
				for (int i = 0; i < lanes.Length; i++)
				{
					SubLane subLane = lanes[i];
					if ((subLane.m_PathMethods & (PathMethod.Parking | PathMethod.BicycleParking)) == 0)
					{
						continue;
					}
					Curve curve2 = m_CurveData[subLane.m_SubLane];
					if (!(math.distancesq(curve2.m_Bezier.d, curve.m_Bezier.a) > 0.0001f))
					{
						PrefabRef prefabRef = m_PrefabRefData[subLane.m_SubLane];
						FitToParkingLane(subLane.m_SubLane, curve2, prefabRef, 0f, out var _, out var blockedMask2, out var slotCount2, out var _, out var _);
						if (slotCount2 != 0 && ((blockedMask2 >> slotCount2 - 1) & 1) == 0L)
						{
							bounds2.min = bounds.min;
						}
						break;
					}
				}
			}
			if (invert)
			{
				blockedMask = math.reversebits(blockedMask) >> 64 - slotCount;
				bounds = 1f - MathUtils.Invert(bounds);
				bounds2 = 1f - MathUtils.Invert(bounds2);
			}
			Bounds1 bounds3 = new Bounds1(bounds2.min, bounds.min);
			float num = MathUtils.Size(bounds) / (float)math.max(1, slotCount);
			for (int j = 0; j < slotCount; j++)
			{
				if (((blockedMask >> j) & 1) != 0L)
				{
					bounds3.min = math.min(bounds3.min, bounds.min + (float)j * num);
					bounds3.max = bounds.min + (float)(j + 1) * num;
					continue;
				}
				if (bounds3.max > bounds3.min)
				{
					ref NativeList<CutRange> reference = ref laneBuffer.m_CutRanges;
					CutRange value = new CutRange
					{
						m_Bounds = bounds3,
						m_Group = uint.MaxValue
					};
					reference.Add(in value);
				}
				bounds3 = new Bounds1(bounds.max, 0f);
			}
			bounds3.max = bounds2.max;
			if (bounds3.max > bounds3.min)
			{
				ref NativeList<CutRange> reference2 = ref laneBuffer.m_CutRanges;
				CutRange value = new CutRange
				{
					m_Bounds = bounds3,
					m_Group = uint.MaxValue
				};
				reference2.Add(in value);
			}
		}

		private void GetCutRanges(Entity lane, SecondaryLaneDataFlags flags, LaneBuffer laneBuffer, Bezier4x3 curve, float2 width, float cutOverlap, bool invert, bool isRight, Entity ignore)
		{
			bool flag = (flags & SecondaryLaneDataFlags.SkipSafePedestrianOverlap) != 0;
			bool flag2 = (flags & (SecondaryLaneDataFlags.SkipSafeCarOverlap | SecondaryLaneDataFlags.SkipUnsafeCarOverlap | SecondaryLaneDataFlags.SkipSideCarOverlap)) != 0;
			bool flag3 = (flags & SecondaryLaneDataFlags.SkipTrackOverlap) != 0;
			bool flag4 = (flags & SecondaryLaneDataFlags.SkipMergeOverlap) != 0;
			if (flag2 && m_CarLaneData.HasComponent(lane))
			{
				CarLane carLane = m_CarLaneData[lane];
				CarLaneFlags carLaneFlags = ((isRight != invert) ? (CarLaneFlags.Roundabout | CarLaneFlags.LeftLimit) : (CarLaneFlags.Roundabout | CarLaneFlags.RightLimit));
				flag2 = (carLane.m_Flags & (carLaneFlags | CarLaneFlags.Approach)) != carLaneFlags;
			}
			if ((!flag && !flag2 && !flag3) || !m_LaneOverlaps.HasBuffer(lane))
			{
				return;
			}
			DynamicBuffer<LaneOverlap> dynamicBuffer = m_LaneOverlaps[lane];
			int length = laneBuffer.m_CutRanges.Length;
			for (int i = 0; i < dynamicBuffer.Length; i++)
			{
				LaneOverlap laneOverlap = dynamicBuffer[i];
				if (((uint)laneOverlap.m_Flags & (uint)((isRight != invert) ? 4 : 8)) == 0 || laneOverlap.m_Other == ignore)
				{
					continue;
				}
				if (!flag || !m_PedestrianLaneData.HasComponent(laneOverlap.m_Other) || (m_PedestrianLaneData[laneOverlap.m_Other].m_Flags & PedestrianLaneFlags.Unsafe) != 0)
				{
					if (flag2 && m_CarLaneData.HasComponent(laneOverlap.m_Other))
					{
						CarLane carLane2 = m_CarLaneData[laneOverlap.m_Other];
						if (((flags & SecondaryLaneDataFlags.SkipSafeCarOverlap) != 0 && (carLane2.m_Flags & CarLaneFlags.Unsafe) == 0) || ((flags & SecondaryLaneDataFlags.SkipUnsafeCarOverlap) != 0 && (carLane2.m_Flags & CarLaneFlags.Unsafe) != 0) || ((flags & SecondaryLaneDataFlags.SkipSideCarOverlap) != 0 && (carLane2.m_Flags & CarLaneFlags.SideConnection) != 0))
						{
							if ((carLane2.m_Flags & (CarLaneFlags.Approach | CarLaneFlags.Roundabout | CarLaneFlags.LeftLimit)) == (CarLaneFlags.Roundabout | CarLaneFlags.LeftLimit))
							{
								laneOverlap.m_Flags &= ~OverlapFlags.OverlapRight;
							}
							if ((carLane2.m_Flags & (CarLaneFlags.Approach | CarLaneFlags.Roundabout | CarLaneFlags.RightLimit)) == (CarLaneFlags.Roundabout | CarLaneFlags.RightLimit))
							{
								laneOverlap.m_Flags &= ~OverlapFlags.OverlapLeft;
							}
							goto IL_029c;
						}
					}
					if (!flag3 || !m_TrackLaneData.HasComponent(laneOverlap.m_Other))
					{
						if (!flag4 || !m_SlaveLaneData.HasComponent(laneOverlap.m_Other))
						{
							continue;
						}
						SlaveLane slaveLane = m_SlaveLaneData[laneOverlap.m_Other];
						if ((slaveLane.m_Flags & SlaveLaneFlags.MergingLane) == 0 || ((flags & (SecondaryLaneDataFlags.SkipSafeCarOverlap | SecondaryLaneDataFlags.SkipUnsafeCarOverlap)) == SecondaryLaneDataFlags.SkipSafeCarOverlap && (!m_OwnerData.TryGetComponent(laneOverlap.m_Other, out var componentData) || !m_SubLanes.TryGetBuffer(componentData.m_Owner, out var bufferData) || bufferData.Length <= slaveLane.m_MasterIndex || !m_CarLaneData.TryGetComponent(bufferData[slaveLane.m_MasterIndex].m_SubLane, out var componentData2) || (componentData2.m_Flags & CarLaneFlags.Unsafe) != 0)))
						{
							continue;
						}
					}
				}
				goto IL_029c;
				IL_0544:
				CutRange value;
				Bounds1 bounds;
				value.m_Bounds |= bounds;
				int num;
				laneBuffer.m_CutRanges[num] = value;
				continue;
				IL_029c:
				Curve curve2 = m_CurveData[laneOverlap.m_Other];
				PrefabRef prefabRef = m_PrefabRefData[laneOverlap.m_Other];
				NetLaneData netLaneData = m_PrefabLaneData[prefabRef.m_Prefab];
				float2 float5 = new float2((int)laneOverlap.m_ThisStart, (int)laneOverlap.m_ThisEnd) * 0.003921569f;
				float5 = math.select(float5, 1f - float5, invert);
				bounds = new Bounds1(1f, 0f);
				int num2 = 0;
				if ((laneOverlap.m_Flags & (OverlapFlags.MergeStart | OverlapFlags.MergeMiddleStart)) != 0)
				{
					bounds |= float5.x;
					num2++;
				}
				if ((laneOverlap.m_Flags & (OverlapFlags.MergeEnd | OverlapFlags.MergeMiddleEnd)) != 0)
				{
					bounds |= float5.y;
					num2++;
				}
				float2 float6 = netLaneData.m_Width;
				if (m_NodeLaneData.TryGetComponent(laneOverlap.m_Other, out var componentData3))
				{
					float6 += componentData3.m_WidthOffset;
				}
				float4 falseValue = new float4(math.min(0f, cutOverlap - float6 * 0.5f), math.max(0f, float6 * 0.5f - cutOverlap));
				falseValue = math.select(falseValue, falseValue.zwxy, (laneOverlap.m_Flags & OverlapFlags.MergeFlip) != 0);
				if ((laneOverlap.m_Flags & OverlapFlags.OverlapLeft) != 0)
				{
					Bezier4x3 curve3 = NetUtils.OffsetCurveLeftSmooth(curve2.m_Bezier, falseValue.xy);
					if (ValidateCurve(curve3) && ExtendedIntersect(curve.xz, curve3.xz, width, float6, out var t))
					{
						bounds |= t.x;
						num2++;
					}
				}
				if ((laneOverlap.m_Flags & OverlapFlags.OverlapRight) != 0)
				{
					Bezier4x3 curve4 = NetUtils.OffsetCurveLeftSmooth(curve2.m_Bezier, falseValue.zw);
					if (ValidateCurve(curve4) && ExtendedIntersect(curve.xz, curve4.xz, width, float6, out var t2))
					{
						bounds |= t2.x;
						num2++;
					}
				}
				if (num2 == 1)
				{
					bounds |= float5.x;
					bounds |= float5.y;
				}
				if (!(bounds.max > bounds.min))
				{
					continue;
				}
				uint num3 = uint.MaxValue;
				if (m_SlaveLaneData.HasComponent(laneOverlap.m_Other))
				{
					num3 = m_SlaveLaneData[laneOverlap.m_Other].m_Group;
					num = length;
					while (num < laneBuffer.m_CutRanges.Length)
					{
						value = laneBuffer.m_CutRanges[num];
						if (value.m_Group != num3)
						{
							num++;
							continue;
						}
						goto IL_0544;
					}
				}
				laneBuffer.m_CutRanges.Add(new CutRange
				{
					m_Bounds = bounds,
					m_Group = num3
				});
			}
		}

		private bool ExtendedIntersect(Bezier4x2 curve1, Bezier4x2 curve2, float2 width1, float2 width2, out float2 t)
		{
			float2 float5 = math.max(new float2(width1.x, width2.x), new float2(width1.y, width2.y));
			if (MathUtils.Intersect(curve1, curve2, out t, 4))
			{
				return true;
			}
			if (MathUtils.Intersect(curve1, new Line2.Segment(curve2.a, curve2.a - math.normalizesafe(MathUtils.StartTangent(curve2)) * (float5.x * 0.5f)), out t, 4) && t.y * float5.x <= math.lerp(width1.x, width1.y, t.x))
			{
				t.y = 0f;
				return true;
			}
			if (MathUtils.Intersect(curve2, new Line2.Segment(curve1.a, curve1.a - math.normalizesafe(MathUtils.StartTangent(curve1)) * (float5.y * 0.5f)), out t, 4) && t.y * float5.y <= math.lerp(width2.x, width2.y, t.x))
			{
				t = new float2(0f, t.x);
				return true;
			}
			if (MathUtils.Intersect(curve1, new Line2.Segment(curve2.d, curve2.d + math.normalizesafe(MathUtils.EndTangent(curve2)) * (float5.x * 0.5f)), out t, 4) && t.y * float5.x <= math.lerp(width1.x, width1.y, t.x))
			{
				t.y = 1f;
				return true;
			}
			if (MathUtils.Intersect(curve2, new Line2.Segment(curve1.d, curve1.d + math.normalizesafe(MathUtils.EndTangent(curve1)) * (float5.y * 0.5f)), out t, 4) && t.y * float5.y <= math.lerp(width2.x, width2.y, t.x))
			{
				t = new float2(1f, t.x);
				return true;
			}
			return false;
		}

		private void ReplaceTempOwner(ref LaneKey laneKey, Entity owner)
		{
			if (m_TempData.HasComponent(owner))
			{
				Temp temp = m_TempData[owner];
				if (temp.m_Original != Entity.Null && (!m_EdgeData.HasComponent(temp.m_Original) || m_EdgeData.HasComponent(owner)))
				{
					laneKey.ReplaceOwner(owner, temp.m_Original);
				}
			}
		}

		private void GetOriginalLane(LaneBuffer laneBuffer, LaneKey laneKey, ref Temp temp)
		{
			if (laneBuffer.m_OriginalLanes.TryGetValue(laneKey, out var item))
			{
				temp.m_Original = item;
				laneBuffer.m_OriginalLanes.Remove(laneKey);
			}
		}

		private bool CheckRequirements(ref LaneBuffer laneBuffer, Entity lanePrefab)
		{
			if (!m_LaneRequirements.HasBuffer(lanePrefab))
			{
				return true;
			}
			DynamicBuffer<ObjectRequirementElement> dynamicBuffer = m_LaneRequirements[lanePrefab];
			if (!laneBuffer.m_RequirementsSearched)
			{
				if (!laneBuffer.m_Requirements.IsCreated)
				{
					laneBuffer.m_Requirements = new NativeParallelHashSet<Entity>(10, Allocator.Temp);
				}
				if (0 == 0 && m_DefaultTheme != Entity.Null)
				{
					laneBuffer.m_Requirements.Add(m_DefaultTheme);
				}
				laneBuffer.m_RequirementsSearched = true;
			}
			int num = -1;
			bool flag = true;
			for (int i = 0; i < dynamicBuffer.Length; i++)
			{
				ObjectRequirementElement objectRequirementElement = dynamicBuffer[i];
				if (objectRequirementElement.m_Group != num)
				{
					if (!flag)
					{
						break;
					}
					num = objectRequirementElement.m_Group;
					flag = false;
				}
				flag |= laneBuffer.m_Requirements.Contains(objectRequirementElement.m_Requirement);
			}
			return flag;
		}

		void IJobChunk.Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
		{
			Execute(in chunk, unfilteredChunkIndex, useEnabledMask, in chunkEnabledMask);
		}
	}

	private struct TypeHandle
	{
		[ReadOnly]
		public EntityTypeHandle __Unity_Entities_Entity_TypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<Node> __Game_Net_Node_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<Deleted> __Game_Common_Deleted_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<EdgeGeometry> __Game_Net_EdgeGeometry_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<Composition> __Game_Net_Composition_RO_ComponentTypeHandle;

		[ReadOnly]
		public ComponentTypeHandle<Temp> __Game_Tools_Temp_RO_ComponentTypeHandle;

		[ReadOnly]
		public BufferTypeHandle<SubLane> __Game_Net_SubLane_RO_BufferTypeHandle;

		[ReadOnly]
		public ComponentLookup<Edge> __Game_Net_Edge_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Curve> __Game_Net_Curve_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Lane> __Game_Net_Lane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CarLane> __Game_Net_CarLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<TrackLane> __Game_Net_TrackLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PedestrianLane> __Game_Net_PedestrianLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ParkingLane> __Game_Net_ParkingLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<MasterLane> __Game_Net_MasterLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<SlaveLane> __Game_Net_SlaveLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<SecondaryLane> __Game_Net_SecondaryLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<EdgeLane> __Game_Net_EdgeLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<NodeLane> __Game_Net_NodeLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<HangingLane> __Game_Net_HangingLane_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Owner> __Game_Common_Owner_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<LaneGeometry> __Game_Net_LaneGeometry_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<CullingInfo> __Game_Rendering_CullingInfo_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<Temp> __Game_Tools_Temp_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<PrefabRef> __Game_Prefabs_PrefabRef_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<NetLaneArchetypeData> __Game_Prefabs_NetLaneArchetypeData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<NetLaneData> __Game_Prefabs_NetLaneData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<SecondaryLaneData> __Game_Prefabs_SecondaryLaneData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<NetLaneGeometryData> __Game_Prefabs_NetLaneGeometryData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<NetCompositionData> __Game_Prefabs_NetCompositionData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<ParkingLaneData> __Game_Prefabs_ParkingLaneData_RO_ComponentLookup;

		[ReadOnly]
		public ComponentLookup<UtilityLaneData> __Game_Prefabs_UtilityLaneData_RO_ComponentLookup;

		[ReadOnly]
		public BufferLookup<SubLane> __Game_Net_SubLane_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<LaneOverlap> __Game_Net_LaneOverlap_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<SecondaryNetLane> __Game_Prefabs_SecondaryNetLane_RO_BufferLookup;

		[ReadOnly]
		public BufferLookup<ObjectRequirementElement> __Game_Prefabs_ObjectRequirementElement_RO_BufferLookup;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void __AssignHandles(ref SystemState state)
		{
			__Unity_Entities_Entity_TypeHandle = state.GetEntityTypeHandle();
			__Game_Net_Node_RO_ComponentTypeHandle = state.GetComponentTypeHandle<Node>(isReadOnly: true);
			__Game_Common_Deleted_RO_ComponentTypeHandle = state.GetComponentTypeHandle<Deleted>(isReadOnly: true);
			__Game_Net_EdgeGeometry_RO_ComponentTypeHandle = state.GetComponentTypeHandle<EdgeGeometry>(isReadOnly: true);
			__Game_Net_Composition_RO_ComponentTypeHandle = state.GetComponentTypeHandle<Composition>(isReadOnly: true);
			__Game_Tools_Temp_RO_ComponentTypeHandle = state.GetComponentTypeHandle<Temp>(isReadOnly: true);
			__Game_Net_SubLane_RO_BufferTypeHandle = state.GetBufferTypeHandle<SubLane>(isReadOnly: true);
			__Game_Net_Edge_RO_ComponentLookup = state.GetComponentLookup<Edge>(isReadOnly: true);
			__Game_Net_Curve_RO_ComponentLookup = state.GetComponentLookup<Curve>(isReadOnly: true);
			__Game_Net_Lane_RO_ComponentLookup = state.GetComponentLookup<Lane>(isReadOnly: true);
			__Game_Net_CarLane_RO_ComponentLookup = state.GetComponentLookup<CarLane>(isReadOnly: true);
			__Game_Net_TrackLane_RO_ComponentLookup = state.GetComponentLookup<TrackLane>(isReadOnly: true);
			__Game_Net_PedestrianLane_RO_ComponentLookup = state.GetComponentLookup<PedestrianLane>(isReadOnly: true);
			__Game_Net_ParkingLane_RO_ComponentLookup = state.GetComponentLookup<ParkingLane>(isReadOnly: true);
			__Game_Net_MasterLane_RO_ComponentLookup = state.GetComponentLookup<MasterLane>(isReadOnly: true);
			__Game_Net_SlaveLane_RO_ComponentLookup = state.GetComponentLookup<SlaveLane>(isReadOnly: true);
			__Game_Net_SecondaryLane_RO_ComponentLookup = state.GetComponentLookup<SecondaryLane>(isReadOnly: true);
			__Game_Net_EdgeLane_RO_ComponentLookup = state.GetComponentLookup<EdgeLane>(isReadOnly: true);
			__Game_Net_NodeLane_RO_ComponentLookup = state.GetComponentLookup<NodeLane>(isReadOnly: true);
			__Game_Net_HangingLane_RO_ComponentLookup = state.GetComponentLookup<HangingLane>(isReadOnly: true);
			__Game_Common_Owner_RO_ComponentLookup = state.GetComponentLookup<Owner>(isReadOnly: true);
			__Game_Net_LaneGeometry_RO_ComponentLookup = state.GetComponentLookup<LaneGeometry>(isReadOnly: true);
			__Game_Rendering_CullingInfo_RO_ComponentLookup = state.GetComponentLookup<CullingInfo>(isReadOnly: true);
			__Game_Tools_Temp_RO_ComponentLookup = state.GetComponentLookup<Temp>(isReadOnly: true);
			__Game_Prefabs_PrefabRef_RO_ComponentLookup = state.GetComponentLookup<PrefabRef>(isReadOnly: true);
			__Game_Prefabs_NetLaneArchetypeData_RO_ComponentLookup = state.GetComponentLookup<NetLaneArchetypeData>(isReadOnly: true);
			__Game_Prefabs_NetLaneData_RO_ComponentLookup = state.GetComponentLookup<NetLaneData>(isReadOnly: true);
			__Game_Prefabs_SecondaryLaneData_RO_ComponentLookup = state.GetComponentLookup<SecondaryLaneData>(isReadOnly: true);
			__Game_Prefabs_NetLaneGeometryData_RO_ComponentLookup = state.GetComponentLookup<NetLaneGeometryData>(isReadOnly: true);
			__Game_Prefabs_NetCompositionData_RO_ComponentLookup = state.GetComponentLookup<NetCompositionData>(isReadOnly: true);
			__Game_Prefabs_ParkingLaneData_RO_ComponentLookup = state.GetComponentLookup<ParkingLaneData>(isReadOnly: true);
			__Game_Prefabs_UtilityLaneData_RO_ComponentLookup = state.GetComponentLookup<UtilityLaneData>(isReadOnly: true);
			__Game_Net_SubLane_RO_BufferLookup = state.GetBufferLookup<SubLane>(isReadOnly: true);
			__Game_Net_LaneOverlap_RO_BufferLookup = state.GetBufferLookup<LaneOverlap>(isReadOnly: true);
			__Game_Prefabs_SecondaryNetLane_RO_BufferLookup = state.GetBufferLookup<SecondaryNetLane>(isReadOnly: true);
			__Game_Prefabs_ObjectRequirementElement_RO_BufferLookup = state.GetBufferLookup<ObjectRequirementElement>(isReadOnly: true);
		}
	}

	private CityConfigurationSystem m_CityConfigurationSystem;

	private ModificationBarrier4B m_ModificationBarrier;

	private EntityQuery m_OwnerQuery;

	private ComponentTypeSet m_AppliedTypes;

	private ComponentTypeSet m_DeletedTempTypes;

	private ComponentTypeSet m_HideLaneTypes;

	private TypeHandle __TypeHandle;

	[Preserve]
	protected override void OnCreate()
	{
		base.OnCreate();
		m_CityConfigurationSystem = base.World.GetOrCreateSystemManaged<CityConfigurationSystem>();
		m_ModificationBarrier = base.World.GetOrCreateSystemManaged<ModificationBarrier4B>();
		m_OwnerQuery = GetEntityQuery(new EntityQueryDesc
		{
			All = new ComponentType[1] { ComponentType.ReadOnly<SubLane>() },
			Any = new ComponentType[2]
			{
				ComponentType.ReadOnly<Updated>(),
				ComponentType.ReadOnly<Deleted>()
			},
			None = new ComponentType[4]
			{
				ComponentType.ReadOnly<OutsideConnection>(),
				ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
				ComponentType.ReadOnly<Building>(),
				ComponentType.ReadOnly<Area>()
			}
		});
		m_AppliedTypes = new ComponentTypeSet(ComponentType.ReadWrite<Applied>(), ComponentType.ReadWrite<Created>(), ComponentType.ReadWrite<Updated>());
		m_DeletedTempTypes = new ComponentTypeSet(ComponentType.ReadWrite<Deleted>(), ComponentType.ReadWrite<Temp>());
		m_HideLaneTypes = new ComponentTypeSet(ComponentType.ReadWrite<CullingInfo>(), ComponentType.ReadWrite<MeshBatch>(), ComponentType.ReadWrite<MeshColor>());
		RequireForUpdate(m_OwnerQuery);
	}

	[Preserve]
	protected override void OnUpdate()
	{
		// Refresh the cached type handles for this frame. Vanilla generates one
		// `InternalCompilerInterface.Get*(ref __TypeHandle.X, ref CheckedStateRef)` per handle which
		// does the same thing (Update + read); a single __AssignHandles call is equivalent.
		__TypeHandle.__AssignHandles(ref base.CheckedStateRef);
		JobHandle jobHandle = JobChunkExtensions.ScheduleParallel(new UpdateLanesJob
		{
			m_EntityType = __TypeHandle.__Unity_Entities_Entity_TypeHandle,
			m_NodeType = __TypeHandle.__Game_Net_Node_RO_ComponentTypeHandle,
			m_DeletedType = __TypeHandle.__Game_Common_Deleted_RO_ComponentTypeHandle,
			m_EdgeGeometryType = __TypeHandle.__Game_Net_EdgeGeometry_RO_ComponentTypeHandle,
			m_CompositionType = __TypeHandle.__Game_Net_Composition_RO_ComponentTypeHandle,
			m_TempType = __TypeHandle.__Game_Tools_Temp_RO_ComponentTypeHandle,
			m_SubLaneType = __TypeHandle.__Game_Net_SubLane_RO_BufferTypeHandle,
			m_EdgeData = __TypeHandle.__Game_Net_Edge_RO_ComponentLookup,
			m_CurveData = __TypeHandle.__Game_Net_Curve_RO_ComponentLookup,
			m_LaneData = __TypeHandle.__Game_Net_Lane_RO_ComponentLookup,
			m_CarLaneData = __TypeHandle.__Game_Net_CarLane_RO_ComponentLookup,
			m_TrackLaneData = __TypeHandle.__Game_Net_TrackLane_RO_ComponentLookup,
			m_PedestrianLaneData = __TypeHandle.__Game_Net_PedestrianLane_RO_ComponentLookup,
			m_ParkingLaneData = __TypeHandle.__Game_Net_ParkingLane_RO_ComponentLookup,
			m_MasterLaneData = __TypeHandle.__Game_Net_MasterLane_RO_ComponentLookup,
			m_SlaveLaneData = __TypeHandle.__Game_Net_SlaveLane_RO_ComponentLookup,
			m_SecondaryLaneData = __TypeHandle.__Game_Net_SecondaryLane_RO_ComponentLookup,
			m_EdgeLaneData = __TypeHandle.__Game_Net_EdgeLane_RO_ComponentLookup,
			m_NodeLaneData = __TypeHandle.__Game_Net_NodeLane_RO_ComponentLookup,
			m_HangingLaneData = __TypeHandle.__Game_Net_HangingLane_RO_ComponentLookup,
			m_OwnerData = __TypeHandle.__Game_Common_Owner_RO_ComponentLookup,
			m_LaneGeometryData = __TypeHandle.__Game_Net_LaneGeometry_RO_ComponentLookup,
			m_CullingInfoData = __TypeHandle.__Game_Rendering_CullingInfo_RO_ComponentLookup,
			m_TempData = __TypeHandle.__Game_Tools_Temp_RO_ComponentLookup,
			m_PrefabRefData = __TypeHandle.__Game_Prefabs_PrefabRef_RO_ComponentLookup,
			m_PrefabLaneArchetypeData = __TypeHandle.__Game_Prefabs_NetLaneArchetypeData_RO_ComponentLookup,
			m_PrefabLaneData = __TypeHandle.__Game_Prefabs_NetLaneData_RO_ComponentLookup,
			m_PrefabSecondaryLaneData = __TypeHandle.__Game_Prefabs_SecondaryLaneData_RO_ComponentLookup,
			m_PrefabLaneGeometryData = __TypeHandle.__Game_Prefabs_NetLaneGeometryData_RO_ComponentLookup,
			m_PrefabCompositionData = __TypeHandle.__Game_Prefabs_NetCompositionData_RO_ComponentLookup,
			m_PrefabParkingLaneData = __TypeHandle.__Game_Prefabs_ParkingLaneData_RO_ComponentLookup,
			m_PrefabUtilityLaneData = __TypeHandle.__Game_Prefabs_UtilityLaneData_RO_ComponentLookup,
			m_SubLanes = __TypeHandle.__Game_Net_SubLane_RO_BufferLookup,
			m_LaneOverlaps = __TypeHandle.__Game_Net_LaneOverlap_RO_BufferLookup,
			m_PrefabSecondaryLanes = __TypeHandle.__Game_Prefabs_SecondaryNetLane_RO_BufferLookup,
			m_LaneRequirements = __TypeHandle.__Game_Prefabs_ObjectRequirementElement_RO_BufferLookup,
			m_DefaultTheme = m_CityConfigurationSystem.defaultTheme,
			m_LeftHandTraffic = m_CityConfigurationSystem.leftHandTraffic,
			m_AppliedTypes = m_AppliedTypes,
			m_DeletedTempTypes = m_DeletedTempTypes,
			m_HideLaneTypes = m_HideLaneTypes,
			m_CommandBuffer = m_ModificationBarrier.CreateCommandBuffer().AsParallelWriter()
		}, m_OwnerQuery, base.Dependency);
		m_ModificationBarrier.AddJobHandleForProducer(jobHandle);
		base.Dependency = jobHandle;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void __AssignQueries(ref SystemState state)
	{
		new EntityQueryBuilder(Allocator.Temp).Dispose();
	}

	protected override void OnCreateForCompiler()
	{
		base.OnCreateForCompiler();
		__AssignQueries(ref base.CheckedStateRef);
		__TypeHandle.__AssignHandles(ref base.CheckedStateRef);
	}

	[Preserve]
	public CustomSecondaryLaneSystem()
	{
	}
}
