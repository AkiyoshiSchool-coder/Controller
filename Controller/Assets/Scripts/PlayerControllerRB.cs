using System.Numerics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerControllerRB : MonoBehaviour
{
    [SerializeField] private float WalkSpeed = 5.5f;
    [SerializeField] private float RunningSpeed = 9f;
    [SerializeField] private float JumpForce = 8f;
    [SerializeField] private float LookSensitivity = 0.2f;
    [SerializeField] private float LookAngleLimit = 90f; 

    [SerializeField] private bool JumpRequest;
    
    [SerializeField] private float GroundChekExtra = 0.1f;
    [SerializeField] private CapsuleCollider capsule;
     [SerializeField] private LayerMask GroundMask;
    private bool isGrounded;

    [SerializeField] private float LookAngle;
    [SerializeField] private Camera MainCamera;

    [SerializeField] private float currentMoveSpeed;
    [SerializeField] private float currentVerticalSpeed;
    [SerializeField] private Rigidbody RB;
    [SerializeField] private UnityEngine.Vector2 moveVector = UnityEngine.Vector2.zero;

    private InputAction MoveAction;
    private InputAction RunAction;
    private InputAction JumpAction;
    private InputAction LookAction;


    void Start()
    {
        RB.freezeRotation = true;

        MoveAction = InputSystem.actions.FindAction("Move");
        RunAction = InputSystem.actions.FindAction("Sprint");
        JumpAction = InputSystem.actions.FindAction("Jump");
        LookAction = InputSystem.actions.FindAction("Look");

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        //Definindo Velocidade
        currentMoveSpeed = WalkSpeed;
    }

    // Update is called once per frame
    void Update()
    {
        moveVector = MoveAction.ReadValue<UnityEngine.Vector2>();

        currentMoveSpeed = RunAction.IsPressed() ? RunningSpeed : WalkSpeed;
        
        if(JumpAction.WasPressedThisFrame())
        {
            JumpRequest = true;
        }
        UnityEngine.Vector2 lookVector = LookAction.ReadValue<UnityEngine.Vector2>();
        HandleCamera(lookVector);
    }

    void FixedUpdate()
    {
        UnityEngine.Vector3 move =  (transform.forward * moveVector.y + transform.right * moveVector.x) * currentMoveSpeed;
        float verticalSpeed = RB.linearVelocity.y;

        float distance = capsule.height / 2f +GroundChekExtra;
        UnityEngine.Vector3 origin = transform.TransformPoint(capsule.center);
        isGrounded = Physics.Raycast(origin,UnityEngine.Vector3.down,distance,GroundMask);

        if(JumpRequest && isGrounded)
        {
            verticalSpeed = JumpForce;
        }
        JumpRequest = false;

        RB.linearVelocity = new UnityEngine.Vector3(move.x,verticalSpeed,move.z);

    }
    private void HandleCamera(UnityEngine.Vector2 LookVector)
    {
        LookAngle += -LookVector.y *LookSensitivity;
        LookAngle = Mathf.Clamp(LookAngle, -LookAngleLimit, LookAngleLimit);
        MainCamera.transform.localRotation = UnityEngine.Quaternion.Euler(LookAngle,0,0);
        transform.rotation *= UnityEngine.Quaternion.Euler(0,LookVector.x*LookSensitivity,0);
    }
}
