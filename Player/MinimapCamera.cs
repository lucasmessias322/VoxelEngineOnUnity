using UnityEngine;
using UnityEngine.UI;

public class MinimapCamera : MonoBehaviour
{
    public GameObject Player;
    [SerializeField] float cameraHeight = 100f;
    [SerializeField] float UpdateInterval = 0.5f;
    [SerializeField] float zoomSpeed = 5f;
    [SerializeField] float minZoom = 20f;
    [SerializeField] float maxZoom = 200f;
    [SerializeField] Image PlayerMarker;

    [SerializeField] float markerScaleFactor = 0.1f; // quanto o ícone deve crescer por unidade de zoom

    private Camera minimapCam;

    void Start()
    {
        minimapCam = GetComponent<Camera>();
        InvokeRepeating("UpdateMiniMapCamera", 0f, UpdateInterval);
    }

    void Update()
    {
        // Zoom in com "," e Zoom out com "."
        if (Input.GetKey(KeyCode.Comma))
        {
            minimapCam.orthographicSize = Mathf.Max(minZoom, minimapCam.orthographicSize - zoomSpeed * Time.deltaTime);
        }

        if (Input.GetKey(KeyCode.Period))
        {
            minimapCam.orthographicSize = Mathf.Min(maxZoom, minimapCam.orthographicSize + zoomSpeed * Time.deltaTime);
        }

        UpdateMarkerScale();
    }

    void UpdateMiniMapCamera()
    {
        if (Player != null)
        {
            transform.position = new Vector3(
                Player.transform.position.x,
                Player.transform.position.y + cameraHeight,
                Player.transform.position.z
            );
        }
    }

    void UpdateMarkerScale()
    {
        if (PlayerMarker != null)
        {
            float scale = minimapCam.orthographicSize * markerScaleFactor;
            PlayerMarker.rectTransform.localScale = new Vector3(scale, scale, 1f);
        }
    }

    
}
