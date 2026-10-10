using Content.Shared._Crescent.ShipShields;
using Robust.Client.ResourceManagement;
using Robust.Client.Graphics;
using Robust.Shared.Enums;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using System.Numerics;
using System.Runtime.InteropServices;
using Content.Client.Resources;
using Robust.Client.Physics;
using Robust.Shared.Prototypes;

namespace Content.Client._Crescent.ShipShields;

public sealed class ShipShieldOverlay : Overlay
{
    private readonly IEntityManager _entManager;
    private readonly FixtureSystem _fixture;
    private readonly SharedPhysicsSystem _physics;
    private readonly ShaderInstance _unshadedShader;
    private readonly Texture _shieldTexture;
    private readonly List<DrawVertexUV2D> _verts = new(128);
    public override OverlaySpace Space => OverlaySpace.WorldSpaceBelowWorld;

    public ShipShieldOverlay(IEntityManager entityManager, IPrototypeManager prototypeManager, IResourceCache resourceCache)
    {
        _entManager = entityManager;
        _fixture = _entManager.EntitySysManager.GetEntitySystem<FixtureSystem>();
        _physics = _entManager.EntitySysManager.GetEntitySystem<PhysicsSystem>();

        _unshadedShader = prototypeManager.Index<ShaderPrototype>("unshaded").Instance();
        _shieldTexture = resourceCache.GetTexture("/Textures/_Crescent/ShipShields/shieldtex.png");

        ZIndex = 8;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;

        handle.UseShader(_unshadedShader);

        var enumerator = _entManager.AllEntityQueryEnumerator<ShipShieldVisualsComponent, FixturesComponent, TransformComponent>();
        while (enumerator.MoveNext(out var uid, out var visuals, out var fixtures, out var xform))
        {
            // VANTABLACK OVERDRAW FROM THE DEPTHS OF LAGHELL
            if (xform.MapID != args.MapId)
                continue;

            var fixture = _fixture.GetFixtureOrNull(uid, "shield", fixtures);

            if (fixture is not { Shape: ChainShape chain })
                continue;

            var transform = _physics.GetPhysicsTransform(uid);

            // Skip geometry for shields outside the viewport.
            if (!IsOnScreen(chain, transform.Position, args.WorldAABB))
                continue;

            DrawShield(handle, chain, transform, xform.LocalPosition, _shieldTexture, _verts);
        }

        // Don't leak the unshaded shader into whatever draws after this overlay.
        handle.UseShader(null);
    }

    private static bool IsOnScreen(ChainShape chain, Vector2 worldCenter, in Box2 viewport)
    {
        var maxDistanceSq = 0f;

        foreach (var vertex in chain.Vertices)
        {
            var distanceSq = vertex.LengthSquared();

            if (distanceSq > maxDistanceSq)
                maxDistanceSq = distanceSq;
        }

        // Keep a small margin around the chain's outer edge.
        var radius = MathF.Sqrt(maxDistanceSq) + 2f;
        var extents = new Vector2(radius, radius);

        return new Box2(worldCenter - extents, worldCenter + extents).Intersects(viewport);
    }

    private void DrawShield(DrawingHandleWorld handle, ChainShape chain, Transform transform, Vector2 localPos, Texture tex, List<DrawVertexUV2D> verts)
    {
        // The vertices of this fixture are defined relative to local position,
        // so we'll have to add them to this and then use the matrix to put them back in world position.
        verts.Clear();

        for (int i = 1; i <= chain.Count; i++)
        {
            // top left corner
            var leftVertex = VertexToWorldPos(chain.Vertices[i - 1], transform);

            // top right corner
            var rightVertex = VertexToWorldPos(chain.Vertices[i], transform);

            // bottom left corner
            var leftCorner = Corner(localPos, leftVertex, transform);

            // bottom right corner
            var rightCorner = Corner(localPos, rightVertex, transform);

            // Assemble 2 triangles.

            // Triangle one: top left, top right, bottom left
            verts.Add(new DrawVertexUV2D(leftVertex, new Vector2(0, 1)));
            verts.Add(new DrawVertexUV2D(rightVertex, new Vector2(1, 1)));
            verts.Add(new DrawVertexUV2D(leftCorner, Vector2.Zero));

            // Triangle two: top right, bottom left, bottom right
            verts.Add(new DrawVertexUV2D(rightVertex, new Vector2(1, 1)));
            verts.Add(new DrawVertexUV2D(leftCorner, Vector2.Zero));
            verts.Add(new DrawVertexUV2D(rightCorner, new Vector2(1, 0)));
        }

        // Draw directly from the reusable vertex buffer.
        handle.DrawPrimitives(DrawPrimitiveTopology.TriangleList, texture: tex, CollectionsMarshal.AsSpan(verts), Color.White);
    }

    private Vector2 VertexToWorldPos(Vector2 vertexPos, Transform transform)
    {
        var vertLocation = Transform.Mul(transform, vertexPos);

        return vertLocation;
    }

    private Vector2 Corner(Vector2 localPos, Vector2 vertexPos, Transform transform, float radius = 1.3f)
    {
        var localXform = Transform.Mul(transform, localPos);
        var cornerPos = Vector2.Subtract(vertexPos, localXform);
        cornerPos.Normalize();
        cornerPos *= radius;

        return Vector2.Subtract(vertexPos, cornerPos);
    }
}
