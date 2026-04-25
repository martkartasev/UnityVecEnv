using UnityEditor;
using UnityEngine;

namespace Editor
{
    public static class SplitSelectedMeshBySubmesh
    {
        [MenuItem("GameObject/Mesh/Split By Submesh", false, 10)]
        private static void Split(MenuCommand command)
        {
            GameObject selected = command.context as GameObject;

            if (selected == null)
            {
                Debug.LogError("No GameObject selected.");
                return;
            }

            MeshFilter sourceFilter = selected.GetComponent<MeshFilter>();
            MeshRenderer sourceRenderer = selected.GetComponent<MeshRenderer>();

            if (sourceFilter == null || sourceRenderer == null)
            {
                Debug.LogError("Selected GameObject must have a MeshFilter and MeshRenderer.");
                return;
            }

            Mesh sourceMesh = sourceFilter.sharedMesh;
            if (sourceMesh == null)
            {
                Debug.LogError("Selected GameObject has no mesh.");
                return;
            }

            Material[] sourceMaterials = sourceRenderer.sharedMaterials;

            GameObject parent = new GameObject(selected.name + "_SplitParts");
            parent.transform.SetParent(selected.transform.parent, false);
            parent.transform.position = selected.transform.position;
            parent.transform.rotation = selected.transform.rotation;
            parent.transform.localScale = selected.transform.localScale;

            for (int i = 0; i < sourceMesh.subMeshCount; i++)
            {
                Mesh partMesh = new Mesh();
                partMesh.name = sourceMesh.name + "_Submesh_" + i;

                partMesh.vertices = sourceMesh.vertices;
                partMesh.normals = sourceMesh.normals;
                partMesh.tangents = sourceMesh.tangents;
                partMesh.colors = sourceMesh.colors;
                partMesh.uv = sourceMesh.uv;
                partMesh.uv2 = sourceMesh.uv2;
                partMesh.uv3 = sourceMesh.uv3;
                partMesh.uv4 = sourceMesh.uv4;

                partMesh.triangles = sourceMesh.GetTriangles(i);
                partMesh.RecalculateBounds();

                GameObject part = new GameObject(selected.name + "_Part_" + i);
                part.transform.SetParent(parent.transform, false);

                MeshFilter partFilter = part.AddComponent<MeshFilter>();
                MeshRenderer partRenderer = part.AddComponent<MeshRenderer>();

                partFilter.sharedMesh = partMesh;

                if (i < sourceMaterials.Length)
                    partRenderer.sharedMaterial = sourceMaterials[i];

                Undo.RegisterCreatedObjectUndo(part, "Split Mesh");
            }

            Undo.RegisterCreatedObjectUndo(parent, "Split Mesh");
        }
    }
}