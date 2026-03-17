using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Mirror
{
    public class NetworkScenePostProcess : MonoBehaviour
    {
        [PostProcessScene]
        public static void OnPostProcessScene()
        {
            // find all NetworkIdentities in all scenes
            // => can't limit it to GetActiveScene() because that wouldn't work
            //    for additive scene loads (the additively loaded scene is never
            //    the active scene)
            // => ignore DontDestroyOnLoad scene! this avoids weird situations
            //    like in NetworkZones when we destroy the local player and
            //    load another scene afterwards, yet the local player is still
            //    in the FindObjectsOfType result with scene=DontDestroyOnLoad
            //    for some reason
            // => OfTypeAll so disabled objects are included too
            // => Unity 2019 returns prefabs here too, so filter them out.
            IEnumerable<NetworkIdentity> identities = Resources.FindObjectsOfTypeAll<NetworkIdentity>()
                .Where(identity => identity.gameObject.hideFlags != HideFlags.NotEditable &&
                                   identity.gameObject.hideFlags != HideFlags.HideAndDontSave &&
                                   identity.gameObject.scene.name != "DontDestroyOnLoad" &&
                                   !Utils.IsPrefab(identity.gameObject));

            foreach (NetworkIdentity identity in identities)
            {
                // if we had a [ConflictComponent] attribute that would be better than this check.
                // also there is no context about which scene this is in.
                if (identity.GetComponent<NetworkManager>() != null)
                    Debug.LogError("NetworkManager has a NetworkIdentity component. This will cause the NetworkManager object to be disabled, so it is not recommended.");

                // not spawned before?
                //  OnPostProcessScene is called after additive scene loads too,
                //  and we don't want to set main scene's objects inactive again
                if (!identity.isClient && !identity.isServer)
                {
                    // valid scene object?
                    //   otherwise it might be an unopened scene that still has null
                    //   sceneIds. builds are interrupted if they contain 0 sceneIds,
                    //   but it's still possible that we call LoadScene in Editor
                    //   for a previously unopened scene.
                    //   (and only do SetActive if this was actually a scene object)
                    
                    if (identity.sceneId != 0)
                    {
                        //AssignSceneID(identity);
                        PrepareSceneObject(identity);
                    }
                    // throwing an exception would only show it for one object
                    // because this function would return afterwards.
                    else
                    {
                        // there are two cases where sceneId == 0:
                        // if we have a prefab open in the prefab scene
                        string path = identity.gameObject.scene.path;
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            // pressing play while in prefab edit mode used to freeze/crash Unity 2019.
                            // this seems fine now so we don't need to stop the editor anymore.
#if UNITY_2020_3_OR_NEWER
                            Debug.LogWarning($"{identity.name} was open in Prefab Edit Mode while launching with Mirror. If this causes issues, please let us know.");
#else
                            Debug.LogError($"{identity.name} is currently open in Prefab Edit Mode. Please open the actual scene before launching Mirror.");
                            EditorApplication.isPlaying = false;
#endif
                        }
                        // if an unopened scene needs resaving
                        else
                        {

                            // nothing good will happen when trying to launch with invalid sceneIds.
                            // show an error and stop playing immediately.
                            Debug.LogError($"Scene {path} needs to be opened and resaved, because the scene object {identity.name} has no valid sceneId yet.");
                            EditorApplication.isPlaying = false;

                        }
                    }
                    
                }
            }
        }

        static void PrepareSceneObject(NetworkIdentity identity)
        {
            // set scene hash
            //identity.SetSceneIdSceneHashPartInternal();
            SetSceneHash(identity);

            // spawnable scene objects are force disabled on scene load to
            // ensure Start/Update/etc. aren't called until actually spawned.
            //
            // note: NetworkIdentity.OnDisable adds itself to the
            //       spawnableObjects dictionary (only if sceneId != 0)
            identity.gameObject.SetActive(false);

            // safety check for prefabs with more than one NetworkIdentity
            GameObject prefabGO = PrefabUtility.GetCorrespondingObjectFromSource(identity.gameObject);
            if (prefabGO)
            {
                GameObject prefabRootGO = prefabGO.transform.root.gameObject;
                if (prefabRootGO != null && prefabRootGO.GetComponentsInChildren<NetworkIdentity>().Length > 1)
                    Debug.LogWarning($"Prefab {prefabRootGO.name} has several NetworkIdentity components attached to itself or its children, this is not supported.");
            }
        }

        static void SetSceneHash(NetworkIdentity identity)
        {
            // Use `ToLower` to that because BuildPipeline.BuildPlayer is case insensitive but hash is case sensitive
            // If the scene in the project is `forest.unity` but `Forest.unity` is given to BuildPipeline then the
            // BuildPipeline will use `Forest.unity` for the build and create a different hash than the editor will.
            // Using ToLower will mean the hash will be the same for these 2 paths
            // Assets/Scenes/Forest.unity
            // Assets/Scenes/forest.unity
            string scenePath = identity.gameObject.scene.path.ToLower();

            // get deterministic scene hash
            uint pathHash = (uint)scenePath.GetStableHashCode();

            // shift hash from 0x000000FFFFFFFF to 0xFFFFFFFF00000000
            ulong shiftedHash = (ulong)pathHash << 32;

            // OR into scene id
            identity.sceneId = (identity.sceneId & 0xFFFFFFFF) | shiftedHash;

            // log it. this is incredibly useful to debug sceneId issues.
            //Debug.Log($"{name} in scene {gameObject.scene.name} scene index hash {pathHash:X} copied into sceneId {sceneId:X}");
        }

        static void AssignSceneID(NetworkIdentity identity)
        {
            Dictionary<ulong, NetworkIdentity> sceneIds = typeof(Mirror.NetworkIdentity).GetField("sceneIds", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).GetValue(identity) as Dictionary<ulong, NetworkIdentity>;
            if (sceneIds == null)
            {
                Debug.LogError("Couldn't get sceneIds dictionary via reflection");
                return;
            }
            // we only ever assign sceneIds at edit time, never at runtime.
            // by definition, only the original scene objects should get one.
            // -> if we assign at runtime then server and client would generate
            //    different random numbers!
            if (Application.isPlaying)
                return;

            // no valid sceneId yet, or duplicate?
            bool duplicate = sceneIds.TryGetValue(identity.sceneId, out NetworkIdentity existing) && existing != null && existing != identity;
            if (identity.sceneId == 0 || duplicate)
            {
                // clear in any case, because it might have been a duplicate
                identity.sceneId = 0;

                // if a scene was never opened and we are building it, then a
                // sceneId would be assigned to build but not saved in editor,
                // resulting in them getting out of sync.
                // => don't ever assign temporary ids. they always need to be
                //    permanent
                // => throw an exception to cancel the build and let the user
                //    know how to fix it!
                
                if (BuildPipeline.isBuildingPlayer)
                    throw new InvalidOperationException($"Scene {identity.gameObject.scene.path} needs to be opened and resaved before building, because the scene object {identity.name} has no valid sceneId yet.");
                
                // if we generate the sceneId then we MUST be sure to set dirty
                // in order to save the scene object properly. otherwise it
                // would be regenerated every time we reopen the scene, and
                // upgrading would be very difficult.
                // -> Undo.RecordObject is the new EditorUtility.SetDirty!
                // -> we need to call it before changing.
                Undo.RecordObject(identity, "Generated SceneId");
                //EditorUtility.SetDirty(identity.gameObject);

                // generate random sceneId part (0x00000000FFFFFFFF)
                uint randomId = Utils.GetTrueRandomUInt();

                // only assign if not a duplicate of an existing scene id
                // (small chance, but possible)
                duplicate = sceneIds.TryGetValue(randomId, out existing) && existing != null && existing != identity;
                if (!duplicate)
                {
                    identity.sceneId = randomId;
                    Debug.Log($"{identity.name} in scene {identity.gameObject.scene.name} sceneId assigned to:{identity.sceneId:X}");
                }
            }

            // add to sceneIds dict no matter what
            // -> even if we didn't generate anything new, because we still need
            //    existing sceneIds in there to check duplicates
            sceneIds[identity.sceneId] = identity;
        }

        [InitializeOnLoadMethod]
        static void Initialize()
        {
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnSceneOpened;
        }

        static void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            IEnumerable<NetworkIdentity> identities = Resources.FindObjectsOfTypeAll<NetworkIdentity>()
                .Where(identity => identity.gameObject.hideFlags != HideFlags.NotEditable &&
                                   identity.gameObject.hideFlags != HideFlags.HideAndDontSave &&
                                   identity.gameObject.scene.name != "DontDestroyOnLoad" &&
                                   !Utils.IsPrefab(identity.gameObject));

            foreach (NetworkIdentity identity in identities)
            {
                // if we had a [ConflictComponent] attribute that would be better than this check.
                // also there is no context about which scene this is in.
                if (identity.GetComponent<NetworkManager>() != null)
                    Debug.LogError("NetworkManager has a NetworkIdentity component. This will cause the NetworkManager object to be disabled, so it is not recommended.");

                if (!identity.isClient && !identity.isServer)
                {
                    AssignSceneID(identity);
                }
            }
        }
    }
}
