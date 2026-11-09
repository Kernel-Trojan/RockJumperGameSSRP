using UnityEngine;

// Goes on every rock. GameManagerVR adds it automatically, so you do not have to.
[RequireComponent(typeof(Rigidbody))]
public class ObstacleVR : MonoBehaviour
{
    public float despawnDistance = 45f;   // remove the rock once it is this far from the player

    static Material baseMaterial;

    void Awake()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        // Make sure there is a trigger collider that follows the rock's shape
        Collider col = GetComponent<Collider>();
        if (col == null)
        {
            MeshFilter mf = GetComponentInChildren<MeshFilter>();
            MeshCollider mc = gameObject.AddComponent<MeshCollider>();
            if (mf != null) mc.sharedMesh = mf.sharedMesh;
            mc.convex = true;
            col = mc;
        }
        else if (col is MeshCollider)
        {
            ((MeshCollider)col).convex = true;
        }
        col.isTrigger = true;

        // Use the project's normal default material (avoids pink materials), then tint it grey
        Renderer r = GetComponentInChildren<Renderer>();
        if (r != null)
        {
            r.sharedMaterial = GetBaseMaterial();
            r.material.color = Color.Lerp(
                new Color(0.38f, 0.37f, 0.35f),
                new Color(0.58f, 0.56f, 0.52f),
                Random.value);
        }
    }

    public static Material GetBaseMaterial()
    {
        if (baseMaterial == null)
        {
            GameObject tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseMaterial = tmp.GetComponent<Renderer>().sharedMaterial;
            Destroy(tmp);
        }
        return baseMaterial;
    }

    void Update()
    {
        // The world flows past the player, so a rock far from the centre is behind us: remove it
        Vector3 p = transform.position;
        p.y = 0f;
        if (p.sqrMagnitude > despawnDistance * despawnDistance) Destroy(gameObject);
    }
}
