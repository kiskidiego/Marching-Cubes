using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using Array = Godot.Collections.Array;


public partial class MapGenerator : Node3D
{
	partial class Random : RandomNumberGenerator
	{
		public Random() { }
		public Random(ulong seed)
		{
			Seed = seed;
		}
		new public int RandiRange(int min, int max)
		{
			Seed++;
			return base.RandiRange(min, max);
		}
		new public float RandfRange(float min, float max)
		{
			Seed++;
			return base.RandfRange(min, max);
		}
	}
	//TODO: Do something about already rendered chunks (they occupy memory)
	class Point
	{
		public Vector3 position;
		public float score = -1;
		public Point(Vector3 position)
		{
			this.position = position;
		}
	}
	class Curve
	{
		public Vector3 startPoint;
		public Vector3 endPoint;
		public Vector3 controlPoint;
	}
	class CurvePoint
	{
		public Vector3 position;
		public CurvePoint(Vector3 position)
		{
			this.position = position;
		}
	}
	class Chunk
	{
		public bool loaded = false;
		public bool rendered = false;
		public Vector3I index;
		public Vector3 worldPosition;
		public List<CurvePoint> curvePoints = new List<CurvePoint>();
		public Chunk(Vector3I index, Vector3 worldPosition)
		{
			this.index = index;
			this.worldPosition = worldPosition;
		}	
	}
	[Export] ulong seed = 0;
	[Export] int worldSize = 100;
	[Export] public int chunkSize { get; private set; } = 10;
	[Export] float cubeSize = 1;
	[Export] Vector3 tunnelOrigin;
	[Export] float tunnelRange = 10;
	[Export] int tunnelAmount = 10;
	[Export] int curveSamples = 10;
	[Export] float surfaceValue = 5;
	[Export] int renderDistance = 5;
	[Export] float branchiness = 0.5f;
	List<Chunk> renderedChunks = new List<Chunk>();
	float sqrSurfaceValue;
	Vector3I lowestChunk;
	Chunk[,,] chunkGrid;
	MeshInstance3D[,,] chunkMeshes;
	Random random;
	float halfworldwidth;
	public override void _Ready()
	{
		if(seed == 0)
		{
			seed = (ulong)DateTime.Now.Ticks;
		}
		random = new Random(seed);
		halfworldwidth = worldSize * chunkSize * cubeSize / 2;
		sqrSurfaceValue = surfaceValue * surfaceValue;
		////GD.Print("Lowest chunk: " + lowestChunk);
		PrepareChunkGrid();
		SampleCurves(PrepareCurves());
		DoChunkOperations(tunnelOrigin);
	}

	void PrepareChunkGrid()
	{
		chunkGrid = new Chunk[worldSize, worldSize, worldSize];
		chunkMeshes = new MeshInstance3D[worldSize, worldSize, worldSize];
		for (int i = 0; i < chunkGrid.GetLength(0); i++)
		{
			for (int j = 0; j < chunkGrid.GetLength(1); j++)
			{
				for (int k = 0; k < chunkGrid.GetLength(2); k++)
				{
					Vector3I chunkIndex = new Vector3I(i, j, k);
					Vector3 chunkWorldPosition = IndexToChunk(chunkIndex);

					chunkGrid[i, j, k] = new Chunk(chunkIndex, chunkWorldPosition);
				}
			}
		}
	}

	public void DoChunkOperations(Vector3 worldPosition)
	{
		Vector3I chunkIndex = ChunkToIndex(worldPosition);
		GD.Print("Chunk index: " + chunkIndex + " World Position: " + worldPosition);

		foreach(Chunk chunk in renderedChunks)
		{
			Vector3I distanceVector = (chunk.index - chunkIndex).Abs();
			if(distanceVector.X >= renderDistance || distanceVector.Y >= renderDistance || distanceVector.Z >= renderDistance)
			{
				UnloadChunk(chunk);
			}
		}

		for(int x = -renderDistance; x <= renderDistance; x++)
		{
			for(int y = -renderDistance; y <= renderDistance; y++)
			{
				for(int z = -renderDistance; z <= renderDistance; z++)
				{
					Vector3I currentChunkIndex = chunkIndex + new Vector3I(x, y, z);
					if (currentChunkIndex.X < 0 || currentChunkIndex.X >= chunkGrid.GetLength(0) || currentChunkIndex.Y < 0 || currentChunkIndex.Y >= chunkGrid.GetLength(1) || currentChunkIndex.Z < 0 || currentChunkIndex.Z >= chunkGrid.GetLength(2))
					{
						continue;
					}
					if (chunkGrid[currentChunkIndex.X, currentChunkIndex.Y, currentChunkIndex.Z].rendered)
					{
						continue;
					}
					GD.Print("Rendering X: " + x + " Y: " + y + " Z: " + z);
					ProcessChunk(chunkGrid[currentChunkIndex.X, currentChunkIndex.Y, currentChunkIndex.Z]);
					renderedChunks.Add(chunkGrid[currentChunkIndex.X, currentChunkIndex.Y, currentChunkIndex.Z]);
					chunkGrid[currentChunkIndex.X, currentChunkIndex.Y, currentChunkIndex.Z].rendered = true;
				}
			}
		}
		for(int i = 0; i < renderedChunks.Count; i++)
		{
			if (!renderedChunks[i].rendered)
			{
				renderedChunks.RemoveAt(i);
				i--;
			}
		}
	}

	void UnloadChunk(Chunk chunk)
	{
		chunk.rendered = false;
		MeshInstance3D meshInstance = chunkMeshes[chunk.index.X, chunk.index.Y, chunk.index.Z];
		if (meshInstance != null && !meshInstance.IsQueuedForDeletion())
		{
			meshInstance.ProcessMode = ProcessModeEnum.Disabled;
			meshInstance.Visible = false;
			StaticBody3D chunkBody = meshInstance.GetNode<StaticBody3D>("StaticBody");
			chunkBody.SetPhysicsProcess(false);
			chunkBody.GetNode<CollisionShape3D>("CollisionShape").Disabled = true;
		}
	}

	void ProcessChunk(Chunk chunk)
	{
		MeshInstance3D meshInstance = chunkMeshes[chunk.index.X, chunk.index.Y, chunk.index.Z];
		if(chunk.loaded && meshInstance != null)
		{
			meshInstance.ProcessMode = ProcessModeEnum.Inherit;
			meshInstance.Visible = true;
			StaticBody3D chunkBody = meshInstance.GetNode<StaticBody3D>("StaticBody");
			chunkBody.SetPhysicsProcess(true);
			chunkBody.GetNode<CollisionShape3D>("CollisionShape").Disabled = false;
		}
		else
		{
			GenerateChunk(chunk);
		}
	}

	void GenerateChunk(Chunk chunk)
	{
		chunk.loaded = true;
		Point[,,] grid = PrepareGrid(chunk);
		//GD.Print("Grid prepared: " + grid.Length);
		if (AssignScores(chunk, grid))
		{
			//GD.Print("if passed");
			MarchingCubesAlgorithm(chunk, grid, sqrSurfaceValue);
		}
	}

	Curve[] PrepareCurves()
	{
		float worldWidth = worldSize * chunkSize * cubeSize;
		Queue<Vector3> tunnelBranchPoints = new Queue<Vector3>();
		Curve[] curves = new Curve[tunnelAmount];
		curves[0] = new Curve();
		curves[0].startPoint = tunnelOrigin;

		float x;
		do
		{
			x = random.RandfRange(-tunnelRange, tunnelRange);
		} while (x > worldWidth / 2 - 2 * sqrSurfaceValue || x < -worldWidth / 2 + 2 * sqrSurfaceValue);
		float y;
		do
		{
			y = random.RandfRange(-tunnelRange, tunnelRange/4);
		} while (y > worldWidth / 2 - 2 * sqrSurfaceValue || y < -worldWidth / 2 + 2 * sqrSurfaceValue);
		float z;
		do
		{
			z = random.RandfRange(-tunnelRange, tunnelRange);
		} while (z > worldWidth / 2 - 2 * sqrSurfaceValue || z < -worldWidth / 2 + 2 * sqrSurfaceValue);
		curves[0].endPoint = new Vector3(x, y, z);


		Vector3 midpoint = (curves[0].startPoint + curves[0].endPoint) / 2;
		do
		{
			x = random.RandfRange(-tunnelRange + midpoint.X, tunnelRange + midpoint.X);
		} while (x > worldWidth / 2 - 2 * sqrSurfaceValue || x < -worldWidth / 2 + 2 * sqrSurfaceValue);
		do
		{
			y = random.RandfRange(-tunnelRange + midpoint.Y, tunnelRange + midpoint.Y);
		} while (y > worldWidth / 2 - 2 * sqrSurfaceValue || y < -worldWidth / 2 + 2 * sqrSurfaceValue);
		do
		{
			z = random.RandfRange(-tunnelRange + midpoint.Z, tunnelRange + midpoint.Z);
		} while (z > worldWidth / 2 - 2 * sqrSurfaceValue || z < -worldWidth / 2 + 2 * sqrSurfaceValue);
		curves[0].controlPoint = new Vector3(x, y, z);

		tunnelBranchPoints.Enqueue(curves[0].endPoint);


		for (int i = 1; i < curves.Length; i++)
		{
			while (random.RandfRange(0, 1) > branchiness)
			{
				Vector3 branchPoint = tunnelBranchPoints.Dequeue();
				tunnelBranchPoints.Enqueue(branchPoint);
			}
			curves[i] = new Curve();
			curves[i].startPoint = tunnelBranchPoints.Dequeue();
			do
			{
				x = random.RandfRange(-tunnelRange + curves[i].startPoint.X, tunnelRange + curves[i].startPoint.X);
			} while (x > worldWidth / 2 - 2 * sqrSurfaceValue || x < -worldWidth / 2 + 2 * sqrSurfaceValue);
			do
			{
				y = random.RandfRange(-tunnelRange + curves[i].startPoint.Y, tunnelRange + curves[i].startPoint.Y);
			} while (y > worldWidth / 2 - 2 * sqrSurfaceValue || y < -worldWidth / 2 + 2 * sqrSurfaceValue);
			do
			{
				z = random.RandfRange(-tunnelRange + curves[i].startPoint.Z, tunnelRange + curves[i].startPoint.Z);
			} while (z > worldWidth / 2 - 2 * sqrSurfaceValue || z < -worldWidth / 2 + 2 * sqrSurfaceValue);
			curves[i].endPoint = new Vector3(x, y, z);
			midpoint = (curves[i].startPoint + curves[i].endPoint) / 2;
			do
			{
				x = random.RandfRange(-tunnelRange + midpoint.X, tunnelRange + midpoint.X);
			} while (x > worldWidth / 2 - 2 * sqrSurfaceValue || x < -worldWidth / 2 + 2 * sqrSurfaceValue);
			do
			{
				y = random.RandfRange(-tunnelRange + midpoint.Y, tunnelRange + midpoint.Y);
			} while (y > worldWidth / 2 - 2 * sqrSurfaceValue || y < -worldWidth / 2 + 2 * sqrSurfaceValue);
			do
			{
				z = random.RandfRange(-tunnelRange + midpoint.Z, tunnelRange + midpoint.Z);
			} while (z > worldWidth / 2 - 2 * sqrSurfaceValue || z < -worldWidth / 2 + 2 * sqrSurfaceValue);
			curves[i].controlPoint = new Vector3(x, y, z);
			tunnelBranchPoints.Enqueue(curves[i].endPoint);
		}
		////GD.Print("Curves prepared");
		return curves;
	}

	void SampleCurves(Curve[] curves)
	{
		for (int curve = 0; curve < tunnelAmount; curve++)
		{
			for (short i = 0; i < curveSamples; i++)
			{

				float t = (float)i / curveSamples;
				Vector3 q0 = curves[curve].startPoint.Lerp(curves[curve].controlPoint, t);
				Vector3 q1 = curves[curve].controlPoint.Lerp(curves[curve].endPoint, t);
				Vector3 point = q0.Lerp(q1, t);
				Vector3I chunk = ChunkToIndex(point);
				//////GD.Print("Curve: " + curve + " Point: " + point + " Chunk: " + chunk);

				if (chunkGrid[chunk.X, chunk.Y, chunk.Z].curvePoints == null)
				{
					chunkGrid[chunk.X, chunk.Y, chunk.Z].curvePoints = new List<CurvePoint>();
				}
				chunkGrid[chunk.X, chunk.Y, chunk.Z].curvePoints.Add(new CurvePoint(q0.Lerp(q1, t)));
			}
		}
		////GD.Print("Curves sampled");
	}

	Point[,,] PrepareGrid(Chunk chunk)
	{
		//GD.Print("Prepare grid");
		Point[,,] grid = new Point[chunkSize + 1, chunkSize + 1, chunkSize + 1];
		//GD.Print(grid.GetLength(0) + " " + grid.GetLength(1) + " " + grid.GetLength(2));
		for (int i = 0; i < grid.GetLength(0); i++)
		{
			for (int j = 0; j < grid.GetLength(1); j++)
			{
				for (int k = 0; k < grid.GetLength(2); k++)
				{
					grid[i, j, k] = new Point(new Vector3(chunk.worldPosition.X + i * cubeSize, chunk.worldPosition.Y + j * cubeSize, chunk.worldPosition.Z + k * cubeSize));
				}
			}
		}
		//GD.Print("Grid prepared at chunk: " + chunk.index);
		return grid;
	}
	
	bool AssignScores(Chunk chunk, Point[,,] grid)
	{
		//GD.Print("Assign scores");
		for (int i = 0; i < grid.GetLength(0); i++)
		{
			for (int j = 0; j <	grid.GetLength(1); j++)
			{
				for (int k = 0; k < grid.GetLength(2); k++)
				{
					if (AssessScore(grid[i, j, k], chunk) == -1)
					{
						return false;
					}
				}
			}
		}
		//GD.Print("Scores assigned at chunk: " + chunk.index);
		return true;
	}

	float AssessScore(Point point, Chunk chunk)
	{
		float score = float.MaxValue;

		int startIndex = -Mathf.CeilToInt(surfaceValue / (chunkSize * cubeSize) + 1);
		int endIndex = -startIndex;

		for (int i = startIndex; i <= endIndex; i++)
		{
			for(int j = startIndex; j <= endIndex; j++)
			{
				for(int k = startIndex; k <= endIndex; k++)
				{
					if(chunk.index.X + i < 0 || chunk.index.X + i >= chunkGrid.GetLength(0) || chunk.index.Y + j < 0 || chunk.index.Y + j >= chunkGrid.GetLength(1) || chunk.index.Z + k < 0 || chunk.index.Z + k >= chunkGrid.GetLength(2))
					{
						continue;
					}
					if (chunkGrid[chunk.index.X + i, chunk.index.Y + j, chunk.index.Z + k].curvePoints == null)
					{
						continue;
					}
					for (int l = 0; l < chunkGrid[chunk.index.X + i, chunk.index.Y + j, chunk.index.Z + k].curvePoints.Count; l++)
					{
						float sqrDistance = (point.position - chunkGrid[chunk.index.X + i, chunk.index.Y + j, chunk.index.Z + k].curvePoints[l].position).LengthSquared();
						if (sqrDistance < score)
						{
							score = sqrDistance;
						}
					}
				}
			}
		}
		if (score < sqrSurfaceValue)
		{
			//GD.Print("Inside");
		}
		if (score == float.MaxValue)
		{
			score = -1;
		}
		////GD.Print("Chunk: " + chunk.index + " Point: " + point.position + " Score: " + score);
		point.score = score;
		return score;
	}

	void MarchingCubesAlgorithm(Chunk chunk, Point[,,] grid, float surfaceValue)
	{
		//GD.Print("Marching cubes algorithm");
		List<Vector3> vertices = new List<Vector3>();
		List<Vector3> normals = new List<Vector3>();

		for (int i = 0; i < grid.GetLength(0) - 1; i++)
		{
			for (int j = 0; j < grid.GetLength(1) - 1; j++)
			{
				for (int k = 0; k < grid.GetLength(2) - 1; k++)
				{
					byte cubeIndex = 0;
					if (grid[i, j, k].score < surfaceValue) 
						cubeIndex |= 1;
					if (grid[i + 1, j, k].score < surfaceValue) 
						cubeIndex |= 2;
					if (grid[i + 1, j, k + 1].score < surfaceValue) 
						cubeIndex |= 4;
					if (grid[i, j, k + 1].score < surfaceValue) 
						cubeIndex |= 8;
					if (grid[i, j + 1, k].score < surfaceValue) 
						cubeIndex |= 16;
					if (grid[i + 1, j + 1, k].score < surfaceValue) 
						cubeIndex |= 32;
					if (grid[i + 1, j + 1, k + 1].score < surfaceValue) 
						cubeIndex |= 64;
					if (grid[i, j + 1, k + 1].score < surfaceValue) 
						cubeIndex |= 128;

					if (cubeIndex == 0 || cubeIndex == 255) 
						continue;

					////GD.Print("Cube index: " + cubeIndex);
					
					if (vertices == null)
					{
						vertices = new List<Vector3>();
						normals = new List<Vector3>();
					}

					Vector3[] edgeVertices = new Vector3[12];
					if ((MarchTables.edges[cubeIndex] & 1) == 1)
					{
						edgeVertices[0] = VertexInterpolation(grid[i, j, k].position, grid[i + 1, j, k].position, grid[i, j, k].score, grid[i + 1, j, k].score);
					}
					if ((MarchTables.edges[cubeIndex] & 2) == 2)
					{
						edgeVertices[1] = VertexInterpolation(grid[i + 1, j, k].position, grid[i + 1, j, k + 1].position, grid[i + 1, j, k].score, grid[i + 1, j, k + 1].score);
					}
					if ((MarchTables.edges[cubeIndex] & 4) == 4)
					{
						edgeVertices[2] = VertexInterpolation(grid[i + 1, j, k + 1].position, grid[i , j, k + 1].position, grid[i + 1, j, k + 1].score, grid[i, j, k + 1].score);
					}
					if ((MarchTables.edges[cubeIndex] & 8) == 8)
					{
						edgeVertices[3] = VertexInterpolation(grid[i, j, k + 1].position, grid[i, j, k].position, grid[i, j, k + 1].score, grid[i, j, k].score);
					}
					if ((MarchTables.edges[cubeIndex] & 16) == 16)
					{
						edgeVertices[4] = VertexInterpolation(grid[i, j + 1, k].position, grid[i + 1, j + 1, k].position, grid[i, j + 1, k].score, grid[i + 1, j + 1, k].score);
					}
					if ((MarchTables.edges[cubeIndex] & 32) == 32)
					{
						edgeVertices[5] = VertexInterpolation(grid[i + 1, j + 1, k].position, grid[i + 1, j + 1, k + 1].position, grid[i + 1, j + 1, k].score, grid[i + 1, j + 1, k + 1].score);
					}
					if ((MarchTables.edges[cubeIndex] & 64) == 64)
					{
						edgeVertices[6] = VertexInterpolation(grid[i + 1, j + 1, k + 1].position, grid[i, j + 1, k + 1].position, grid[i + 1, j + 1, k + 1].score, grid[i, j + 1, k + 1].score);
					}
					if ((MarchTables.edges[cubeIndex] & 128) == 128)
					{
						edgeVertices[7] = VertexInterpolation(grid[i, j + 1, k + 1].position, grid[i, j + 1, k].position, grid[i, j + 1, k + 1].score, grid[i, j + 1, k].score);
					}
					if ((MarchTables.edges[cubeIndex] & 256) == 256)
					{
						edgeVertices[8] = VertexInterpolation(grid[i, j, k].position, grid[i, j + 1, k].position, grid[i, j, k].score, grid[i, j + 1, k].score);
					}
					if ((MarchTables.edges[cubeIndex] & 512) == 512)
					{
						edgeVertices[9] = VertexInterpolation(grid[i + 1, j, k].position, grid[i + 1, j + 1, k].position, grid[i + 1, j, k].score, grid[i + 1, j + 1, k].score);
					}
					if ((MarchTables.edges[cubeIndex] & 1024) == 1024)
					{
						edgeVertices[10] = VertexInterpolation(grid[i + 1, j, k + 1].position, grid[i + 1, j + 1, k + 1].position, grid[i + 1, j, k + 1].score, grid[i + 1, j + 1, k + 1].score);
					}
					if ((MarchTables.edges[cubeIndex] & 2048) == 2048)
					{
						edgeVertices[11] = VertexInterpolation(grid[i, j, k + 1].position, grid[i, j + 1, k + 1].position, grid[i, j, k + 1].score, grid[i, j + 1, k + 1].score);
					}
					
					for (int l = 0; MarchTables.triangles[cubeIndex, l] != -1; l += 3)
					{
						vertices.Add(edgeVertices[MarchTables.triangles[cubeIndex, l]]);
						vertices.Add(edgeVertices[MarchTables.triangles[cubeIndex, l + 1]]);
						vertices.Add(edgeVertices[MarchTables.triangles[cubeIndex, l + 2]]);
						Vector3 normal = (vertices[vertices.Count - 3] - vertices[vertices.Count - 2]).Cross(vertices[vertices.Count - 1] - vertices[vertices.Count - 2]).Normalized();
						normals.Add(normal);
						normals.Add(normal);
						normals.Add(normal);
					}
				}
			}
		}
		////GD.Print("Marching cubes algorithm done at chunk: " + chunk.index + vertices[0][0]);
		if(vertices.Count > 0)
			GenerateMesh(chunk, vertices, normals);
	}

	void GenerateMesh(Chunk chunk, List<Vector3> vertices, List<Vector3> normals)
	{
		GD.Print("Generate mesh: " + vertices.Count);
		MeshInstance3D meshInstance = new MeshInstance3D();
		meshInstance.Mesh = new ArrayMesh();
		CollisionShape3D collisionShape = new CollisionShape3D();
		collisionShape.Name = "CollisionShape";
		StaticBody3D chunkBody = new StaticBody3D();
		chunkBody.Name = "StaticBody";

		GD.Print(chunkBody.Name + " " + collisionShape.Name);
		chunkBody.AddChild(collisionShape);
		meshInstance.AddChild(chunkBody);
		chunkMeshes[chunk.index.X, chunk.index.Y, chunk.index.Z] = meshInstance;
		CallDeferred(Node.MethodName.AddChild, meshInstance);
		//AddChild(meshInstance);
		//meshInstance.Owner = this;

		Array arrays = new Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
		arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
		(meshInstance.Mesh as ArrayMesh).AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

		collisionShape.Shape = meshInstance.Mesh.CreateTrimeshShape();
		meshInstance.Name = "Chunk " + chunk.index;
	}

	public Vector3I ChunkToIndex(Vector3 chunk)
	{
		return new Vector3I((int)MathF.Floor((chunk.X + halfworldwidth) / (chunkSize * cubeSize)), (int)MathF.Floor((chunk.Y + halfworldwidth) / (chunkSize * cubeSize)), (int)MathF.Floor((chunk.Z + halfworldwidth) / (chunkSize * cubeSize)));
	}

	Vector3 IndexToChunk(Vector3I index)
	{
		return new Vector3(index.X * (chunkSize * cubeSize) - halfworldwidth, index.Y * (chunkSize * cubeSize) - halfworldwidth, index.Z * (chunkSize * cubeSize) - halfworldwidth);
	}

	Vector3 VertexInterpolation(Vector3 p1, Vector3 p2, float v1, float v2)
	{
		//return p1 + (p2 - p1) * (surfaceValue - v1) / (v2 - v1);
		////GD.Print("Positions: " + p1 + " " + p2 + " Values: "+ v1 + " " + v2 + " Interpolation: " + (surfaceValue - v1) / (v2 - v1));
		return p1.Lerp(p2, (sqrSurfaceValue - v1) / (v2 - v1));
	}
}