using UnityEngine;

// A second component with serialized fields: referenced from the scene, cloned and created at run time by Exercise.
public class Marker : MonoBehaviour
{
    public int id;
    public string label;

    void Awake() => Debug.Log("PROBE x.marker.awake id=" + id + " label=" + label);

    void OnDestroy() => Debug.Log("PROBE x.marker.destroy id=" + id);
}
