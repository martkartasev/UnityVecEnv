using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

// Source; https://gist.github.com/FreyaHolmer/650ecd551562352120445513efa1d952
// with some mods.


[RequireComponent(typeof(Camera))]
public class FlyCamera : MonoBehaviour
{
    public float acceleration = 50; // how fast you accelerate
    public float accSprintMultiplier = 4; // how much faster you go when "sprinting"
    public float lookSensitivity = 1; // mouse look sensitivity
    public float dampingCoefficient = 5; // how quickly you break to a halt after you stop your input
    public bool focusOnEnable = true; // whether or not to focus and lock cursor immediately on enable

    Vector3 velocity; // current velocity

    Camera cam;

    static bool Focused
    {
        get => Cursor.lockState == CursorLockMode.Locked;
        set
        {
            Cursor.lockState = value ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = value == false;
        }
    }

    void OnEnable()
    {
        if (focusOnEnable) Focused = true;
        cam = GetComponentInParent<Camera>();
    }

    void OnDisable() => Focused = false;

    void Update()
    {
        // Input
        if (Focused)
            UpdateInput();
        else if (cam.enabled && GetMouseButtonDown(1))
            Focused = true;

        // Physics
        velocity = Vector3.Lerp(velocity, Vector3.zero, dampingCoefficient * Time.deltaTime);
        transform.position += velocity * Time.deltaTime;
    }

    void UpdateInput()
    {
        // Position
        velocity += GetAccelerationVector() * Time.deltaTime;

        // Rotation
        Vector2 mouseDelta = lookSensitivity * new Vector2(GetMouseAxis("Mouse X"), -GetMouseAxis("Mouse Y"));
        Quaternion rotation = transform.rotation;
        Quaternion horiz = Quaternion.AngleAxis(mouseDelta.x, Vector3.up);
        Quaternion vert = Quaternion.AngleAxis(mouseDelta.y, Vector3.right);
        transform.rotation = horiz * rotation * vert;

        // Leave cursor lock
        if (GetMouseButtonUp(1))
            Focused = false;
    }

    Vector3 GetAccelerationVector()
    {
        Vector3 moveInput = default;

        void AddMovement(KeyCode key, Vector3 dir)
        {
            if (GetKey(key))
                moveInput += dir;
        }

        AddMovement(KeyCode.W, Vector3.forward);
        AddMovement(KeyCode.UpArrow, Vector3.forward);
        AddMovement(KeyCode.S, Vector3.back);
        AddMovement(KeyCode.DownArrow, Vector3.back);
        AddMovement(KeyCode.D, Vector3.right);
        AddMovement(KeyCode.RightArrow, Vector3.right);
        AddMovement(KeyCode.A, Vector3.left);
        AddMovement(KeyCode.LeftArrow, Vector3.left);
        AddMovement(KeyCode.E, Vector3.up);
        AddMovement(KeyCode.KeypadPlus, Vector3.up);
        AddMovement(KeyCode.Q, Vector3.down);
        AddMovement(KeyCode.KeypadMinus, Vector3.down);
        Vector3 direction = transform.TransformVector(moveInput.normalized);

        if (GetKey(KeyCode.LeftShift))
            return direction * (acceleration * accSprintMultiplier); // "sprinting"
        return direction * acceleration; // "walking"
    }

    // Input abstraction helpers

    static bool GetKey(KeyCode key)
    {
#if ENABLE_INPUT_SYSTEM
        return KeyCodeToKey(key)?.isPressed ?? false;
#else
        return Input.GetKey(key);
#endif
    }

    static bool GetMouseButtonDown(int button)
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null) return false;
        return button switch
        {
            0 => mouse.leftButton.wasPressedThisFrame,
            1 => mouse.rightButton.wasPressedThisFrame,
            2 => mouse.middleButton.wasPressedThisFrame,
            _ => false
        };
#else
        return Input.GetMouseButtonDown(button);
#endif
    }

    static bool GetMouseButtonUp(int button)
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null) return false;
        return button switch
        {
            0 => mouse.leftButton.wasReleasedThisFrame,
            1 => mouse.rightButton.wasReleasedThisFrame,
            2 => mouse.middleButton.wasReleasedThisFrame,
            _ => false
        };
#else
        return Input.GetMouseButtonUp(button);
#endif
    }

    static float GetMouseAxis(string axisName)
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null) return 0f;
        Vector2 delta = mouse.delta.ReadValue();
        return axisName == "Mouse X" ? delta.x : delta.y;
#else
        return Input.GetAxis(axisName);
#endif
    }

#if ENABLE_INPUT_SYSTEM
    static UnityEngine.InputSystem.Controls.KeyControl KeyCodeToKey(KeyCode key)
    {
        var kb = Keyboard.current;
        if (kb == null) return null;
        return key switch
        {
            KeyCode.W           => kb.wKey,
            KeyCode.A           => kb.aKey,
            KeyCode.S           => kb.sKey,
            KeyCode.D           => kb.dKey,
            KeyCode.E           => kb.eKey,
            KeyCode.Q           => kb.qKey,
            KeyCode.UpArrow     => kb.upArrowKey,
            KeyCode.DownArrow   => kb.downArrowKey,
            KeyCode.LeftArrow   => kb.leftArrowKey,
            KeyCode.RightArrow  => kb.rightArrowKey,
            KeyCode.LeftShift   => kb.leftShiftKey,
            KeyCode.KeypadPlus  => kb.numpadPlusKey,
            KeyCode.KeypadMinus => kb.numpadMinusKey,
            _                   => null
        };
    }
#endif
}
