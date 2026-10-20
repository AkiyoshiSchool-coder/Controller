using System.Numerics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

[RequireComponent(typeof(CharacterController))]
public class PlayerControllerCC : MonoBehaviour
{
    //Controle
    [SerializeField] private CharacterController characterController;   
    [SerializeField] private float WalkSpeed = 5.5f;
    [SerializeField] private float RunningSpeed = 9f;
    [SerializeField] private float JumpForce = 8f;
    [SerializeField] private float Gravity = 20f;
    [SerializeField] private float LookSensitivity = 0.2f;
    [SerializeField] private float LookAngleLimit = 90f; 

    //Ações
    private InputAction MoveAction;
    private InputAction RunAction;
    private InputAction JumpAction;
    private InputAction LookAction;

    //Valores atuais
    [SerializeField] private float currentMoveSpeed;
    [SerializeField] private float currentVerticalSpeed;
    [SerializeField] private float LookAngle;

    //Câmera
    [SerializeField] private Camera MainCamera;
    void Start()
    {
        //Declarando ações
        MoveAction = InputSystem.actions.FindAction("Move");
        RunAction = InputSystem.actions.FindAction("Sprint");
        JumpAction = InputSystem.actions.FindAction("Jump");
        LookAction = InputSystem.actions.FindAction("Look");
        
        //visibilidade do cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        //Definindo Velocidade
        currentMoveSpeed = WalkSpeed;
    }

    // Update is called once per frame
    void Update()
    {
        UnityEngine.Vector2 Movement = MoveAction.ReadValue<UnityEngine.Vector2>();
        //running
        currentMoveSpeed = RunAction.IsPressed() ? RunningSpeed : WalkSpeed;
        HandleMovement(Movement);

        //Aplica Câmera
        UnityEngine.Vector2 lookVector = LookAction.ReadValue<UnityEngine.Vector2>();
        HandleCamera(lookVector);
    }
    private void HandleMovement(UnityEngine.Vector2 Movement)
    {
        //Movimentação com direção ajustada pela rotação
        UnityEngine.Vector3 moveDirection = (transform.forward * Movement.y + transform.right * Movement.x) * currentMoveSpeed;

        if(characterController.isGrounded) // Checa se player no chão
        {
            if(JumpAction.WasPressedThisFrame())
            {
                currentVerticalSpeed = JumpForce;
            }
            else
            {
                currentVerticalSpeed = -2f; //Mantém contanto com o chão
            }
        }
        else
        {
            currentVerticalSpeed -= Gravity*Time.deltaTime;
        }

        moveDirection.y = currentVerticalSpeed;

        //Aplica a move
        characterController.Move(moveDirection*Time.deltaTime); 
    }

    private void HandleCamera(UnityEngine.Vector2 LookVector)
    {
        LookAngle += -LookVector.y *LookSensitivity;
        LookAngle = Mathf.Clamp(LookAngle, -LookAngleLimit, LookAngleLimit);
        MainCamera.transform.localRotation = UnityEngine.Quaternion.Euler(LookAngle,0,0);
        transform.rotation *= UnityEngine.Quaternion.Euler(0,LookVector.x*LookSensitivity,0);
    }
}
