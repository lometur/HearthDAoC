#include "DetourInterface.hpp"
#include <cfloat>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <DetourAlloc.h>
#include <DetourCommon.h>
#include <DetourNavMesh.h>
#include <DetourNavMeshQuery.h>
#include <DetourNode.h> // HearthDAoC: dtNodePool, for MoveAlongSurfaceLayered (#166)
#include <DetourStatus.h>
#include <functional>
#include <random>
#include <vector>

// RAII helper
struct RAII
{
	std::function<void()> cleaner;
	RAII(std::function<void()> cleaner) : cleaner(cleaner) {}
	~RAII() { this->cleaner(); }
};

// missing from Detour?
struct dtNavMeshSetHeader
{
	std::int32_t magic;
	std::int32_t version;
	std::int32_t numTiles;
	dtNavMeshParams params;
};

struct dtNavMeshTileHeader
{
	dtTileRef ref;
	std::int32_t size;
};

DLLEXPORT bool LoadNavMesh(char const *file, dtNavMesh **const mesh)
{
	// load the file
	auto fp = std::fopen(file, "rb");
	if (!fp)
		return false;

	// scope for fp closing
	{
		auto _fpRAII = RAII([=]
							{ std::fclose(fp); });

		dtNavMeshSetHeader header;
		fread(&header, sizeof(header), 1, fp);

		if (header.magic != 0x4d534554 || header.version != 1)
			return false;

		// init mesh and query
		*mesh = dtAllocNavMesh();
		auto status = (*mesh)->init(&header.params);
		if (dtStatusFailed(status))
		{
			dtFreeNavMesh(*mesh);
			*mesh = nullptr;
			return false;
		}
		if (header.numTiles > 0)
		{
			auto tileIdx = 0;
			while (tileIdx < header.numTiles)
			{
				dtNavMeshTileHeader tileHeader;
				fread(&tileHeader, sizeof(tileHeader), 1, fp);
				void *data;
				if (tileHeader.ref == 0 || tileHeader.size == 0 || (data = dtAlloc(tileHeader.size, DT_ALLOC_PERM)) == 0)
					break;
				memset(data, 0, tileHeader.size);
				fread(data, tileHeader.size, 1, fp);
				(*mesh)->addTile((unsigned char *)data, tileHeader.size, 1, tileHeader.ref, nullptr);
				tileIdx += 1;
			}
		}
	}
	return true;
}

DLLEXPORT bool FreeNavMesh(dtNavMesh *meshPtr)
{
	if (meshPtr)
		dtFreeNavMesh(meshPtr);
	return true;
}

DLLEXPORT bool CreateNavMeshQuery(dtNavMesh *mesh, dtNavMeshQuery **const query)
{
	*query = dtAllocNavMeshQuery();
	auto status = (*query)->init(mesh, MAX_NODES);
	if (dtStatusFailed(status))
	{
		dtFreeNavMeshQuery(*query);
		*query = nullptr;
		return false;
	}
	return true;
}

DLLEXPORT bool FreeNavMeshQuery(dtNavMeshQuery *queryPtr)
{
	if (queryPtr)
		dtFreeNavMeshQuery(queryPtr);
	return true;
}

DLLEXPORT dtStatus PathStraight(dtNavMeshQuery *query, float start[], float end[], float polyPickExt[], dtPolyFlags queryFilter[], dtStraightPathOptions pathOptions, int *pointCount, float *pointBuffer, dtPolyFlags *pointFlags)
{
	*pointCount = 0;

	dtPolyRef startRef;
	dtPolyRef endRef;
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);

	dtStatus status;

	if (dtStatusSucceed(status = query->findNearestPoly(start, polyPickExt, &filter, &startRef, nullptr)) &&
		dtStatusSucceed(status = query->findNearestPoly(end, polyPickExt, &filter, &endRef, nullptr)))
	{
		int npolys = 0;
		dtPolyRef polys[MAX_POLY];
		dtStatus pathStatus = query->findPath(startRef, endRef, start, end, &filter, polys, &npolys, MAX_POLY);

		if (npolys <= 0)
			return pathStatus;

		if (dtStatusSucceed(pathStatus))
		{
			// Partial if findPath said so, OR if the last polygon found is not the target endRef.
			bool isPartialPath = (pathStatus & DT_PARTIAL_RESULT) != 0;
			if (polys[npolys - 1] != endRef)
				isPartialPath = true;

			dtPolyRef straightPathPolys[MAX_POLY];
			unsigned char straightPathFlags[MAX_POLY];
			status = query->findStraightPath(start, end, polys, npolys, pointBuffer, straightPathFlags, straightPathPolys, pointCount, MAX_POLY, pathOptions);

			if (dtStatusSucceed(status) && (*pointCount > 0))
			{
				for (int i = 0; i < *pointCount; ++i)
				{
					dtPolyRef ref = straightPathPolys[i];

					// Fall back to closest known corridor poly if ref is null.
					if (ref == 0)
						ref = (i == 0) ? polys[0] : (i >= npolys ? polys[npolys - 1] : polys[i]);

					unsigned short flags = 0;
					if (ref != 0)
						query->getAttachedNavMesh()->getPolyFlags(ref, &flags);

					pointFlags[i] = (dtPolyFlags) flags;
				}

				return isPartialPath ? (DT_SUCCESS | DT_PARTIAL_RESULT) : DT_SUCCESS;
			}
		}
		else
			status = pathStatus;
	}

	return status;
}

DLLEXPORT dtStatus MoveAlongSurface(dtNavMeshQuery* query, float start[], float end[], float polyPickExt[], dtPolyFlags queryFilter[], float* outputVector)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	dtPolyRef startRef;
	dtStatus status = query->findNearestPoly(start, polyPickExt, &filter, &startRef, nullptr);
	if (dtStatusFailed(status)) return status;
	float resultPos[3];
	dtPolyRef visited[16];
	int nvisited = 0;
	status = query->moveAlongSurface(startRef, start, end, &filter, resultPos, visited, &nvisited, 16);
	if (dtStatusSucceed(status)) dtVcopy(outputVector, resultPos);
	return status;
}

// Opt-in bot ground planning. Preserve the original 2D API for all other callers.
DLLEXPORT dtStatus MoveAlongSurfaceGrounded(dtNavMeshQuery* query, float start[], float end[], float polyPickExt[], dtPolyFlags queryFilter[], float* outputVector)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);

	dtPolyRef startRef;
	dtStatus status = query->findNearestPoly(start, polyPickExt, &filter, &startRef, nullptr);

	if (dtStatusFailed(status))
		return status;

	float resultPos[3];
	// moveAlongSurface is a 2D query: resultPos retains the input height.
	// Its endpoint must be grounded on the polygon actually visited, not a
	// nearest-poly query which could select a roof or another side of a wall.
	dtPolyRef visited[64];
	int nvisited = 0;

	status = query->moveAlongSurface(startRef, start, end, &filter, resultPos, visited, &nvisited, 64);

	if (dtStatusSucceed(status))
	{
		if (nvisited == 0 || (status & DT_BUFFER_TOO_SMALL) != 0)
			return DT_FAILURE | DT_BUFFER_TOO_SMALL;
		float height;
		dtStatus heightStatus = query->getPolyHeight(visited[nvisited - 1], resultPos, &height);
		if (dtStatusFailed(heightStatus))
			return heightStatus;
		resultPos[1] = height;
		dtVcopy(outputVector, resultPos);
	}

	return status;
}

thread_local std::mt19937 rngMt = std::mt19937(std::random_device{}());
thread_local std::uniform_real_distribution<float> rng(0.0f, 1.0f);

float frand()
{
	return rng(rngMt);
}

DLLEXPORT dtStatus FindRandomPointAroundCircle(dtNavMeshQuery *query, float center[], float radius, float polyPickExt[], dtPolyFlags queryFilter[], float *outputVector)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	dtPolyRef centerRef;
	auto status = query->findNearestPoly(center, polyPickExt, &filter, &centerRef, nullptr);
	if (dtStatusSucceed(status))
	{
		dtPolyRef outRef;
		status = query->findRandomPointAroundCircle(centerRef, center, radius, &filter, frand, &outRef, outputVector);
	}
	return status;
}

DLLEXPORT dtStatus FindClosestPoint(dtNavMeshQuery *query, float center[], float polyPickExt[], dtPolyFlags queryFilter[], float *outputVector)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	dtPolyRef centerRef;
	auto status = query->findNearestPoly(center, polyPickExt, &filter, &centerRef, nullptr);
	if (dtStatusSucceed(status))
		status = query->closestPointOnPoly(centerRef, center, outputVector, nullptr);
	return status;
}

DLLEXPORT dtStatus FindClosestPointInBox(dtNavMeshQuery *query, float boxCenter[], float boxExtents[], float referencePos[], dtPolyFlags queryFilter[], float *outputVector)
{
	const int MAX_POLYS = 32; // This limit can easily be reached, but this function shouldn't be used for large boxes.

	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);

	dtPolyRef polys[MAX_POLYS];
	int polyCount = 0;

	// Find all polygons overlapping the box.
	dtStatus status = query->queryPolygons(boxCenter, boxExtents, &filter, polys, &polyCount, MAX_POLYS);

	// Preserve detail flags from the query status.
	dtStatus detailFlags = status & DT_STATUS_DETAIL_MASK;

	if (dtStatusSucceed(status) && polyCount > 0)
	{
		float minDistSq = FLT_MAX;
		bool found = false;
		float tempVec[3];

		// Small tolerance to handle potential floating point errors on the edges.
		const float EPSILON = 1e-4f;

		for (int i = 0; i < polyCount; ++i)
		{
			// Find closest point on this specific polygon.
			query->closestPointOnPoly(polys[i], referencePos, tempVec, nullptr);

			// Ensure point is actually inside the box.
			bool isInsideBox = true;
			for (int axis = 0; axis < 3; ++axis)
			{
				float minBound = boxCenter[axis] - boxExtents[axis] - EPSILON;
				float maxBound = boxCenter[axis] + boxExtents[axis] + EPSILON;

				if (tempVec[axis] < minBound || tempVec[axis] > maxBound)
				{
					isInsideBox = false;
					break;
				}
			}

			if (!isInsideBox)
				continue; // Point is on a valid polygon, but the point itself is outside our volume.

			// Check distance.
			float dx = tempVec[0] - referencePos[0];
			float dy = tempVec[1] - referencePos[1];
			float dz = tempVec[2] - referencePos[2];
			float dSq = dx * dx + dy * dy + dz * dz;

			if (dSq < minDistSq)
			{
				minDistSq = dSq;
				outputVector[0] = tempVec[0];
				outputVector[1] = tempVec[1];
				outputVector[2] = tempVec[2];
				found = true;
			}
		}

		if (found)
			return DT_SUCCESS | detailFlags;
	}

	return DT_FAILURE | detailFlags;
}

DLLEXPORT dtStatus HasLineOfSight(dtNavMeshQuery* query, float start[], float end[], float polyPickExt[], dtPolyFlags queryFilter[], bool *hasLos, float *outputVector)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);

	dtPolyRef startRef;
	dtStatus status = query->findNearestPoly(start, polyPickExt, &filter, &startRef, nullptr);

	if (dtStatusFailed(status))
		return status;

	dtRaycastHit raycastHit{};
	status = query->raycast(startRef, start, end, &filter, 0, &raycastHit, 0);

	if (dtStatusSucceed(status))
	{
		outputVector[0] = start[0] + (end[0] - start[0]) * raycastHit.t;
		outputVector[1] = start[1] + (end[1] - start[1]) * raycastHit.t;
		outputVector[2] = start[2] + (end[2] - start[2]) * raycastHit.t;
		*hasLos = raycastHit.t > 1.0f - 1e-4f;
	}

	return status;
}

DLLEXPORT dtStatus UpdateFlags(dtNavMesh* navMesh, dtPolyRef polyRefs[], int polyCount, unsigned short flagsToRemove, unsigned short flagsToAdd)
{
	if (polyCount <= 0)
		return DT_SUCCESS;

	dtStatus status;
	std::vector<unsigned short> originalFlags;
	originalFlags.reserve(polyCount);

	for (int i = 0; i < polyCount; ++i)
	{
		unsigned short flags;
		status = navMesh->getPolyFlags(polyRefs[i], &flags);

		if (dtStatusFailed(status))
			return status;

		originalFlags.push_back(flags);
	}

	for (int i = 0; i < polyCount; ++i)
	{
		status = navMesh->setPolyFlags(polyRefs[i], (originalFlags[i] & ~flagsToRemove) | flagsToAdd);

		if (dtStatusFailed(status))
		{
			// Best-effort rollback.
			for (int j = 0; j < i; ++j)
				navMesh->setPolyFlags(polyRefs[j], originalFlags[j]);

			return status;
		}
	}

	return status;
}

DLLEXPORT dtStatus GetPolyAt(dtNavMeshQuery *query, float center[], float polyPickExt[], dtPolyFlags queryFilter[], dtPolyRef polyRef[], float *point)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	return query->findNearestPoly(center, polyPickExt, &filter, polyRef, point);
}

DLLEXPORT dtStatus GetPolysInBox(dtNavMeshQuery *query, float center[], float polyPickExt[], dtPolyFlags queryFilter[], dtPolyRef polyRefs[], int *polyCount, int maxPolys)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	return query->queryPolygons(center, polyPickExt, &filter, polyRefs, polyCount, maxPolys);
}

// HearthDAoC: queries for the player bots (#166) that keep a walker on its own floor. When tiles join at load, Detour
// links a seam edge to any edge across the seam within 2 x walkableClimb at either end of their overlap (overlapSlabs,
// DetourNavMesh.cpp:56-61), so in Jordheim small floors walled in under a ramp are linked only to the ramp 70-152 units
// above them (within a tile Recast links nothing over walkableClimb). The surface move is 2D
// and enters the first linked polygon whose footprint holds its target, the floor first: player bots dropped 80 units
// through the ramp onto such a floor, and their corridor out went back up the same seam, where the straight path's
// corner takes the lower polygon's height and hides the jump. These two queries treat such a link as what it is,
// a change of floor: the move does not take it, and the straight path reports it as two corners, one on each floor.
// NPCs and GameBots keep the queries above.
constexpr unsigned short LAYER_LINK = 0x40; // run-time point flag (EDtPolyFlags.LayerLink): the upper or lower end of a layer link

// The height of a polygon's floor at pt (2D): its detail surface there, or at the nearest point of its detail edges when
// pt lies outside it. Not its vertices': on steep ground the polygon lies tens of units off the floor, so a slope that
// runs on across a tile seam in Snowdonia and in Gripklosa looked like two floors 30-67 units apart.
static float FloorHeightAt(const dtNavMeshQuery* query, dtPolyRef ref, const float* pt)
{
	float closest[3];
	return dtStatusSucceed(query->closestPointOnPoly(ref, pt, closest, nullptr)) ? closest[1] : pt[1];
}

// A link's portal on its polygon's edge vj-vi, clamped as dtNavMeshQuery::getPortalPoints clamps it (a private member,
// DetourNavMeshQuery.cpp:2315-2330).
static void LinkPortal(const dtLink* link, const float* vj, const float* vi, float* a, float* b)
{
	dtVcopy(a, vj);
	dtVcopy(b, vi);
	if (link->side != 0xff && (link->bmin != 0 || link->bmax != 255))
	{
		dtVlerp(a, vj, vi, link->bmin / 255.0f);
		dtVlerp(b, vj, vi, link->bmax / 255.0f);
	}
}

// dtQueryFilter::passFilter, which DetourNavMeshQuery.cpp defines inline (DetourNavMeshQuery.cpp:87-92).
static bool PassFilter(const dtQueryFilter& filter, const dtPoly* poly)
{
	return (poly->flags & filter.getIncludeFlags()) != 0 && (poly->flags & filter.getExcludeFlags()) == 0;
}

// Whether the floors of two polygons lie more than walkableClimb apart at x, a point of the portal between them.
static bool IsLayerGap(const dtNavMeshQuery* query, dtPolyRef ref, dtPolyRef neiRef, const float* x, float climb)
{
	return dtAbs(FloorHeightAt(query, neiRef, x) - FloorHeightAt(query, ref, x)) > climb;
}

// Whether a link of a polygon (ref) is a layer link: a tile-seam link between two ground polygons whose floors lie more
// than walkableClimb apart at both ends of the portal (a, b: the portal on the polygon's edge). The same test for the
// move and the straight path, so that a link one walls off the other never takes. Not a link inside a tile (side
// 0xff), nor an off-mesh connection's (edge 0xff, or to or from its polygon). In Jordheim the floors walled in under a
// ramp are 70-152 units below it at both ends of their links.
static bool IsLayerLink(const dtNavMeshQuery* query, const dtMeshTile* tile, const dtPoly* poly, dtPolyRef ref,
	const dtLink* link, const dtPoly* neiPoly, float* a, float* b)
{
	if (link->side == 0xff || link->edge >= poly->vertCount ||
		poly->getType() != DT_POLYTYPE_GROUND || neiPoly->getType() != DT_POLYTYPE_GROUND)
		return false;
	LinkPortal(link, &tile->verts[poly->verts[link->edge] * 3],
		&tile->verts[poly->verts[(link->edge + 1) % poly->vertCount] * 3], a, b);
	const float climb = tile->header->walkableClimb;
	return IsLayerGap(query, ref, link->ref, a, climb) && IsLayerGap(query, ref, link->ref, b, climb);
}

// MoveAlongSurface (grounded = 0) or MoveAlongSurfaceGrounded (grounded = 1), with dtNavMeshQuery::moveAlongSurface
// (DetourNavMeshQuery.cpp:2044-2232) copied here so that a layer link is a wall: the move slides along its portal as
// along any wall, also where other links share its edge (there a first version of this copy counted the edge as neither
// wall nor way through, where Detour's own move walks through). Every other link, and the rest of the search, is Detour's.
DLLEXPORT dtStatus MoveAlongSurfaceLayered(dtNavMeshQuery* query, float start[], float end[], float polyPickExt[], dtPolyFlags queryFilter[], int grounded, float* outputVector)
{
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	dtPolyRef startRef;
	dtStatus status = query->findNearestPoly(start, polyPickExt, &filter, &startRef, nullptr);
	if (dtStatusFailed(status))
		return status;
	if (!startRef || !dtVisfinite(start) || !dtVisfinite(end))
		return DT_FAILURE | DT_INVALID_PARAM;

	const dtNavMesh* nav = query->getAttachedNavMesh();
	thread_local dtNodePool pool(64, 32); // as the query's tiny node pool (DetourNavMeshQuery.cpp:197)
	pool.clear();
	static const int MAX_STACK = 48;
	dtNode* stack[MAX_STACK];
	int nstack = 0;
	dtNode* startNode = pool.getNode(startRef);
	startNode->pidx = 0;
	startNode->cost = 0;
	startNode->total = 0;
	startNode->id = startRef;
	startNode->flags = DT_NODE_CLOSED;
	stack[nstack++] = startNode;

	float bestPos[3];
	float bestDist = FLT_MAX;
	dtNode* bestNode = 0;
	dtVcopy(bestPos, start);
	float searchPos[3];
	dtVlerp(searchPos, start, end, 0.5f);
	const float searchRadSqr = dtSqr(dtVdist(start, end) / 2.0f + 0.001f);
	float verts[DT_VERTS_PER_POLYGON * 3];

	while (nstack)
	{
		dtNode* curNode = stack[0];
		for (int i = 0; i < nstack - 1; ++i)
			stack[i] = stack[i + 1];
		nstack--;
		const dtMeshTile* curTile = 0;
		const dtPoly* curPoly = 0;
		nav->getTileAndPolyByRefUnsafe(curNode->id, &curTile, &curPoly);
		const int nverts = curPoly->vertCount;
		for (int i = 0; i < nverts; ++i)
			dtVcopy(&verts[i * 3], &curTile->verts[curPoly->verts[i] * 3]);
		if (dtPointInPolygon(end, verts, nverts))
		{
			bestNode = curNode;
			dtVcopy(bestPos, end);
			break;
		}
		for (int i = 0, j = nverts - 1; i < nverts; j = i++)
		{
			static const int MAX_NEIS = 8;
			int nneis = 0;
			dtPolyRef neis[MAX_NEIS];
			if (curPoly->neis[j] & DT_EXT_LINK)
			{
				for (unsigned int k = curPoly->firstLink; k != DT_NULL_LINK; k = curTile->links[k].next)
				{
					const dtLink* link = &curTile->links[k];
					if (link->edge != j || link->ref == 0)
						continue;
					const dtMeshTile* neiTile = 0;
					const dtPoly* neiPoly = 0;
					nav->getTileAndPolyByRefUnsafe(link->ref, &neiTile, &neiPoly);
					float a[3], b[3], t;
					if (!PassFilter(filter, neiPoly))
						continue;
					if (!IsLayerLink(query, curTile, curPoly, curNode->id, link, neiPoly, a, b))
					{
						if (nneis < MAX_NEIS)
							neis[nneis++] = link->ref;
						continue;
					}
					const float distSqr = dtDistancePtSegSqr2D(end, a, b, t);
					if (distSqr < bestDist)
					{
						dtVlerp(bestPos, a, b, t);
						bestDist = distSqr;
						bestNode = curNode;
					}
				}
			}
			else if (curPoly->neis[j])
			{
				const unsigned int idx = (unsigned int)(curPoly->neis[j] - 1);
				const dtPolyRef ref = nav->getPolyRefBase(curTile) | idx;
				if (PassFilter(filter, &curTile->polys[idx]))
					neis[nneis++] = ref;
			}
			const float* vj = &verts[j * 3];
			const float* vi = &verts[i * 3];
			float tseg;
			if (!nneis)
			{
				const float distSqr = dtDistancePtSegSqr2D(end, vj, vi, tseg);
				if (distSqr < bestDist)
				{
					dtVlerp(bestPos, vj, vi, tseg);
					bestDist = distSqr;
					bestNode = curNode;
				}
			}
			else
			{
				for (int k = 0; k < nneis; ++k)
				{
					dtNode* neighbourNode = pool.getNode(neis[k]);
					if (!neighbourNode || (neighbourNode->flags & DT_NODE_CLOSED))
						continue;
					if (dtDistancePtSegSqr2D(searchPos, vj, vi, tseg) > searchRadSqr)
						continue;
					if (nstack < MAX_STACK)
					{
						neighbourNode->pidx = pool.getNodeIdx(curNode);
						neighbourNode->flags |= DT_NODE_CLOSED;
						stack[nstack++] = neighbourNode;
					}
				}
			}
		}
	}

	if (grounded)
	{
		// As MoveAlongSurfaceGrounded: the height of the polygon the move ended on.
		float height;
		if (!bestNode)
			return DT_FAILURE | DT_BUFFER_TOO_SMALL;
		status = query->getPolyHeight(bestNode->id, bestPos, &height);
		if (dtStatusFailed(status))
			return status;
		bestPos[1] = height;
	}
	dtVcopy(outputVector, bestPos);
	return DT_SUCCESS;
}

// PathStraight, with the corridor split where it takes a layer link (IsLayerLink), at the portal's point nearest to
// where the part before it starts. Each part is string-pulled on its own floor, and the crossing is reported twice, on
// the floor it leaves and, flagged LAYER_LINK, on the floor it enters. Without a layer link in the corridor the corners
// are PathStraight's, each put on a floor (below); two at one spot that end on one floor count once.
DLLEXPORT dtStatus PathStraightLayered(dtNavMeshQuery* query, float start[], float end[], float polyPickExt[], dtPolyFlags queryFilter[], dtStraightPathOptions pathOptions, int* pointCount, float* pointBuffer, dtPolyFlags* pointFlags)
{
	*pointCount = 0;
	dtQueryFilter filter;
	filter.setIncludeFlags(queryFilter[0]);
	filter.setExcludeFlags(queryFilter[1]);
	dtPolyRef startRef, endRef;
	dtStatus status;
	if (dtStatusFailed(status = query->findNearestPoly(start, polyPickExt, &filter, &startRef, nullptr)) ||
		dtStatusFailed(status = query->findNearestPoly(end, polyPickExt, &filter, &endRef, nullptr)))
		return status;
	int npolys = 0;
	dtPolyRef polys[MAX_POLY];
	const dtStatus pathStatus = query->findPath(startRef, endRef, start, end, &filter, polys, &npolys, MAX_POLY);
	if (npolys <= 0 || dtStatusFailed(pathStatus))
		return pathStatus;
	const bool isPartialPath = (pathStatus & DT_PARTIAL_RESULT) != 0 || polys[npolys - 1] != endRef;

	const dtNavMesh* nav = query->getAttachedNavMesh();
	float from[3];
	dtVcopy(from, start);
	int first = 0;
	for (int i = 0; i < npolys; ++i)
	{
		float to[3], top[3];
		const bool last = i + 1 == npolys;
		if (last)
			dtVcopy(to, end);
		else
		{
			const dtMeshTile *tile = 0, *neiTile = 0;
			const dtPoly *poly = 0, *neiPoly = 0;
			nav->getTileAndPolyByRefUnsafe(polys[i], &tile, &poly);
			nav->getTileAndPolyByRefUnsafe(polys[i + 1], &neiTile, &neiPoly);
			const dtLink* link = 0;
			for (unsigned int k = poly->firstLink; k != DT_NULL_LINK && !link; k = tile->links[k].next)
				if (tile->links[k].ref == polys[i + 1])
					link = &tile->links[k];
			float left[3], right[3], t;
			if (!link || !IsLayerLink(query, tile, poly, polys[i], link, neiPoly, left, right))
				continue; // the same floor on both sides
			dtDistancePtSegSqr2D(from, left, right, t);
			dtVlerp(to, left, right, t);
			dtVcopy(top, to);
			to[1] = FloorHeightAt(query, polys[i], to);
			top[1] = FloorHeightAt(query, polys[i + 1], to);
		}
		// The part polys[first..i], from 'from' to 'to', with PathStraight's flags.
		float points[MAX_POLY * 3];
		unsigned char straightFlags[MAX_POLY];
		dtPolyRef refs[MAX_POLY];
		const int count = i - first + 1;
		int n = 0;
		status = query->findStraightPath(from, to, polys + first, count, points, straightFlags, refs, &n, MAX_POLY, pathOptions);
		if (dtStatusFailed(status) || n <= 0)
			return dtStatusFailed(status) ? status : DT_FAILURE;
		// findStraightPath clamps its ends to the polygons' edges, at the vertices' height: the crossing keeps its floor's,
		// and a part after a layer link starts at the link's top, given already (not again 72 units off it, in Jordheim).
		if (!last && (straightFlags[n - 1] & DT_STRAIGHTPATH_END))
			points[(n - 1) * 3 + 1] = to[1];
		// Every corner on a floor, not at its vertices' height, which on steep ground lies tens of units off it: in
		// Jordheim a corner 47 units over a floor walled in under a ramp and 50 under the ramp took the walker that reached
		// it onto that floor, the nearer (the review). Not the walker's start, its goal, nor an off-mesh link's. The floor
		// is its polygon's, but where the floors on the two sides of the corner's portal part there, it is the one before
		// the portal, which the walker reaches the corner on; its next step takes it onto the polygon's. At such seams in
		// Gripklosa a tile's detail edge drops 170 units where the other tile's floor does not, and the walker sank into
		// the slope toward the corner (the review of the detail floors); the vertices lay 47-65 units off every floor, and
		// it went back and forth under them (the third review); and a corner on a ledge 81 units over the slope that led
		// up to it took the walker onto a piece of the ledge with no way on (the fourth review).
		// The floor before is the polygon's before the corner's in the corridor, or, where the corridor crosses that one
		// only at this point (a sliver of no area on the seam, entered by the corner before at the same spot), the one
		// before that: the sliver's floor lay 35 and 48 units under the floors on its two sides there (the fourth review).
		for (int k = first > 0 ? 1 : 0; k < n; ++k)
		{
			const dtMeshTile* cornerTile = 0;
			const dtPoly* cornerPoly = 0;
			if (!refs[k] || (straightFlags[k] & (DT_STRAIGHTPATH_START | DT_STRAIGHTPATH_END | DT_STRAIGHTPATH_OFFMESH_CONNECTION)) ||
				dtStatusFailed(nav->getTileAndPolyByRef(refs[k], &cornerTile, &cornerPoly)) || cornerPoly->getType() != DT_POLYTYPE_GROUND)
				continue;
			const float floor = FloorHeightAt(query, refs[k], &points[k * 3]);
			dtPolyRef before = 0; // the corridor's polygon before the one the corner enters
			for (int j = first + 1; j <= i && !before; ++j)
				if (polys[j] == refs[k])
					before = k > 0 && j > first + 1 && refs[k - 1] == polys[j - 1] &&
						dtVdist2DSqr(&points[(k - 1) * 3], &points[k * 3]) < dtSqr(1.0f / 16384.0f) ? polys[j - 2] : polys[j - 1];
			const float other = before ? FloorHeightAt(query, before, &points[k * 3]) : floor;
			points[k * 3 + 1] = dtAbs(other - floor) > cornerTile->header->walkableClimb ? other : floor;
		}
		for (int k = first > 0 ? 1 : 0; k < n + (last ? 0 : 1) && *pointCount < MAX_POLY; ++k)
		{
			const float* point = k < n ? &points[k * 3] : top;
			if (*pointCount > 0 && dtVequal(&pointBuffer[(*pointCount - 1) * 3], point))
				continue;
			dtPolyRef ref = k < n ? refs[k] : polys[i + 1];
			if (ref == 0)
				ref = k == 0 ? polys[first] : (k >= count ? polys[i] : polys[first + k]);
			unsigned short flags = 0;
			nav->getPolyFlags(ref, &flags);
			dtVcopy(&pointBuffer[*pointCount * 3], point);
			pointFlags[(*pointCount)++] = (dtPolyFlags)(k < n ? flags : flags | LAYER_LINK);
		}
		if (!last)
			dtVcopy(from, top);
		first = i + 1;
	}
	return isPartialPath ? (DT_SUCCESS | DT_PARTIAL_RESULT) : DT_SUCCESS;
}
