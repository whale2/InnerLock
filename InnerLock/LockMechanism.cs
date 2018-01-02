using System;
using UnityEngine;
using System.Collections;

namespace InnerLock
{
	public class LockMechanism : PartModule
	{

		// Runtime parameters
		[KSPField (isPersistant = true)]
		public bool isActive = false;

		[KSPField (isPersistant = true)]
		public bool isLocked = false;

		[KSPField (isPersistant = true)]
		public uint latchPartId = 0;

		// Config parameters
		[KSPField (isPersistant = true)]
		public bool canLockToOtherShip = false;

		// TODO: Implement free attaching magnet
		[KSPField (isPersistant = true)]
		public bool ecConstantDrain = false;

		[KSPField (isPersistant = true)]
		public float ecConsumption = 1f;

		[KSPField (isPersistant = true)]
		public string lockingTo = "lockLatch";

		[KSPField (isPersistant = true)]
		public float lockStrength = 50f;

		[KSPField (isPersistant = true)]
		public float minOffset = 0.01f;

		[KSPField (isPersistant = true)]
		public float minRoll = 0.99f;

		// Runtime attributes
		public FixedJoint lockJoint;

		private FXGroup lockSound;
		private FXGroup unlockSound;

		public string unlockSoundPath = "InnerLock/Sounds/unlock";
		public string lockSoundPath = "InnerLock/Sounds/lock";

		// Runtime flags
		private bool latchSlipped = false;
		private bool lockStarted = false;
		private bool msgPosted = false;
		
		public LockMechanism ()
		{
		}

		public override void OnStart (StartState state)
		{
			OnStart (state);
			if (state != StartState.Editor) {
				GameEvents.onVesselGoOffRails.Add (offRails);
				GameEvents.onVesselGoOnRails.Add (onRails);
			}
		}

		public void OnDestroy ()
		{
			GameEvents.onVesselGoOffRails.Remove (offRails);
			GameEvents.onVesselGoOnRails.Remove (onRails);
		}

		[KSPEvent (guiName = "Disengage Lock", guiActive = true, guiActiveEditor = false, name = "disengageLock")]
		public void disengageLock ()
		{
			isActive = true;
			unlockHasp ();
			Events ["engageLock"].active = true;
			Events ["disengageLock"].active = false;
		}

		[KSPEvent (guiName = "Engage Lock", guiActive = true, guiActiveEditor = false, name = "engageLock")]
		public void engageLock ()
		{
			isActive = true;
			Events ["engageLock"].active = false;
			Events ["disengageLock"].active = true;
		}

		public void onRails (Vessel v)
		{
			if (v != vessel)
				return;
			base.Events ["engageLock"].active = false;
			base.Events ["disengageLock"].active = false;
		}

		public void offRails (Vessel v)
		{
			if (v != vessel)
				return;
			Events ["engageLock"].active = !isActive;
			Events ["disengageLock"].active = isActive;
			if (isActive && isLocked && lockJoint == null && latchPartId != 0) {
				Part part = FlightGlobals.FindPartByID (latchPartId);
				if (part != null)
					lockHasp (part, true);
			}
		}

		public void OnCollisionEnter (Collision c)
		{
			if (isActive && !isLocked && !lockStarted)
				this.lockToLatch (c);
		}

		public void OnCollisionExit (Collision c)
		{
			msgPosted = false;
			latchSlipped = true;
			lockStarted = false;
		}

		public void OnCollisionStay (Collision c)
		{
			if (isActive && !isLocked && !lockStarted)
				this.lockToLatch (c);
		}

		public void lockToLatch (Collision c)
		{
			foreach(ContactPoint cp in c.contacts) {

				Part p = cp.otherCollider.attachedRigidbody.GetComponent<Part> ();
				if (p == null)
					continue;
			
				if (!p.name.Equals(lockingTo))
					continue;

				if (!canLockToOtherShip && p.vessel != vessel) {
					Debug.Log ("LockMechanism: canLockToOtherShip = " + canLockToOtherShip + "; other vessel = " + p.vessel.name);
					continue;
				}

				float dotup = Vector3.Dot (cp.normal, transform.up);
				float dotfwd = Math.Abs (Vector3.Dot (p.transform.forward, transform.forward));
				float offset = Vector3.Distance (
		               Vector3.ProjectOnPlane (transform.position, transform.up), 
		               Vector3.ProjectOnPlane (p.transform.position, transform.up));
	
				if (-dotup < minRoll || dotfwd < minRoll || offset > minOffset) {
							
					if (!msgPosted) {
						Debug.Log ("LockMechanism: dotup = " + dotup + "; dotfwd = " + dotfwd + "; offset = " + offset);
						ScreenMessages.PostScreenMessage ("Latch not aligned - can't lock");
						msgPosted = true;
					}
					break;
				}

				latchSlipped = false;
				lockStarted = true;
				lockHasp (p, false);
				break;
			}
		}

		public void lockHasp (Part latch, bool isRelock)
		{
			Debug.Log ("LockMechanism: lockHasp; part = " + latch.name);
			bool flag = !isRelock;
			if (!isRelock) {
				float num = part.RequestResource ("ElectricCharge", this.ecConsumption);
				if (num < this.ecConsumption) {
					ScreenMessages.PostScreenMessage ("Not enough electric charge to lock the hasp!");
					return;
				}
				if (lockSound == null)
					lockSound = this.createAudio (base.part.gameObject, this.lockSoundPath);
				this.lockSound.audio.Play ();
			}
			StartCoroutine (this.finalizeLock (latch, isRelock));
		}

		public IEnumerator finalizeLock (Part latch, bool isRelock)
		{
			Debug.Log ("LockMechanism: finalize lock");
			if (!isRelock)
				yield return new WaitForSeconds (lockSound.audio.clip.length);
			
			if (latchSlipped) {
				ScreenMessages.PostScreenMessage ("Latch slipped! Can't lock");
				yield break;
			}

			Debug.Log ("LockMechanism: creating joint");
			lockJoint = part.gameObject.AddComponent<FixedJoint> ();
			lockJoint.connectedBody = latch.rb;
			lockJoint.breakForce = lockStrength;
			lockJoint.breakTorque = lockStrength;
			Debug.Log ("LockMechanism: locked");
			if (!isRelock)
				ScreenMessages.PostScreenMessage ("Latch locked");

			latchPartId = latch.flightID;
			lockStarted = false;
			isLocked = true;
		}

		public void unlockHasp ()
		{
			if (unlockSound == null)
				unlockSound = createAudio (part.gameObject, unlockSoundPath);
			
			if (isLocked) {
				unlockSound.audio.Play ();
				StartCoroutine (finalizeUnlock ());
			}
		}


		private IEnumerator finalizeUnlock ()
		{
			Debug.Log ("LockMechanism: finalize unlock");
			yield return new WaitForSeconds (unlockSound.audio.clip.length);
			Debug.Log ("LockMechanism: destroying joint");
			Destroy (lockJoint);
			isLocked = false;
			isActive = false;
			latchPartId = 0;
			Debug.Log ("LockMechanism: unlocked");
		}


		private FXGroup createAudio (GameObject obj, string audioPath)
		{
			if (obj == null)
				return null;

			FXGroup fXGroup = new FXGroup ("lockHasp");
			fXGroup.audio = obj.AddComponent<AudioSource> ();
			fXGroup.audio.volume = GameSettings.SHIP_VOLUME;
			fXGroup.audio.rolloffMode = AudioRolloffMode.Logarithmic;
			fXGroup.audio.dopplerLevel = 0;
			fXGroup.audio.maxDistance = 30;
			fXGroup.audio.loop = false;
			fXGroup.audio.playOnAwake = false;

			if (GameDatabase.Instance.ExistsAudioClip (audioPath)) {
				fXGroup.audio.clip = GameDatabase.Instance.GetAudioClip (audioPath);
			}
			else {
				Debug.Log ("LockMechanism: No clip found with path " + audioPath);
			}
			return fXGroup;
		}
	}
}

