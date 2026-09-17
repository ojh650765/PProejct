using PokeLab.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Overworld.World
{
    // Scene bookkeeping only: crossing an outdoor boundary never moves the player,
    // replaces the camera, freezes input or runs a screen transition.
    public sealed class OutdoorRegion : MonoBehaviour
    {
        public static readonly Vector3 RouteOrigin = new Vector3(-32, 0, 8);
        private PlayerLocomotion _player;

        private void Update()
        {
            if (!SceneManager.GetSceneByName("Town").isLoaded) return;
            if (ServiceHub.TryGet<IGameFlow>(out var flow) &&
                flow.Mode != GameMode.Exploring && flow.Mode != GameMode.Dialogue && flow.Mode != GameMode.Cutscene) return;
            var active=SceneManager.GetActiveScene().name;
            if(active!="Town" && active!="Field" && active!="Route202")return;
            if(_player==null)_player=FindFirstObjectByType<PlayerLocomotion>();
            if(_player==null)return;
            var p=_player.transform.position;
            bool inside=p.x>-48 && p.x<-16 && p.z>=8.4f && p.z<80;
            if(inside && active!="Route202")SceneManager.SetActiveScene(gameObject.scene);
            else if(active=="Route202" && p.z<7.6f)
                SceneManager.SetActiveScene(SceneManager.GetSceneByName("Town"));
        }
    }
}
