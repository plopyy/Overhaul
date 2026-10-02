using System.Linq;
using UnityEngine;

namespace Overhaul.AI
{
    internal static class CampHorn
    {
        private static AudioClip clip;
        internal static AudioClip Clip()
        {
            if(clip)return clip;
            const int rate=22050;var samples=new float[rate*3];double phase=0;
            for(int i=0;i<samples.Length;i++)
            {
                float t=i/(float)rate;
                float envelope=Mathf.SmoothStep(0,1,t/.2f)*Mathf.SmoothStep(0,1,(3-t)/.7f);
                float frequency=105+7*Mathf.Clamp01(t/.35f)+.7f*Mathf.Sin(t*27);
                phase+=2*System.Math.PI*frequency/rate;
                // A sustained breathy brass tone, with a soft onset and release.
                float tone=(float)(System.Math.Sin(phase)+.5*System.Math.Sin(phase*2)+.28*System.Math.Sin(phase*3)+.15*System.Math.Sin(phase*4));
                samples[i]=tone*.38f*envelope*(.96f+.04f*Mathf.Sin(t*43));
            }
            clip=AudioClip.Create("Overhaul goblin camp horn",samples.Length,1,rate,false);clip.SetData(samples,0);return clip;
        }
        internal static void Play(Vector3 position)
        {
            if(ZNet.instance&&ZNet.instance.IsDedicated())return;
            if(!Player.m_localPlayer||(Player.m_localPlayer.transform.position-position).sqrMagnitude>22500)return;
            var go=new GameObject("Overhaul camp horn");go.transform.position=position;
            var audio=go.AddComponent<AudioSource>();audio.clip=Clip();audio.spatialBlend=1;audio.minDistance=12;audio.maxDistance=150;
            audio.rolloffMode=AudioRolloffMode.Linear;audio.dopplerLevel=0;audio.volume=.85f;
            var native=ZNetScene.instance?ZNetScene.instance.GetPrefab("sfx_goblin_alerted"):null;
            var source=native?native.GetComponentInChildren<AudioSource>(true):null;
            if(source)audio.outputAudioMixerGroup=source.outputAudioMixerGroup;
            else if(AudioMan.instance)audio.outputAudioMixerGroup=AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX").FirstOrDefault();
            audio.Play();Object.Destroy(go,4);
        }
    }
}
