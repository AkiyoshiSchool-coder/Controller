using UnityEngine;

public class RocketMove : MonoBehaviour
{
    public bool activated;
    [SerializeField] private float step = 0.5f;
    [SerializeField] private GameObject target;
    [SerializeField] private GameObject Particle;
    [SerializeField] private GameObject SParticle;
    // Update is called once per frame
    void Update()
    {
        if(activated)
        {
            transform.position = Vector3.MoveTowards(transform.position, target.transform.position, step);
            Particle.SetActive(true);
        }
    }

    public void OnPointerClick()
    {
        activated = true;
    }
    public void OnPointerEnter()
    {
        SParticle.SetActive(true);
        print("aaaaaa");
    }
    public void OnPointerExit()
    {
        SParticle.SetActive(false);
        print("aaaaaaaaaaaa");
    }
}