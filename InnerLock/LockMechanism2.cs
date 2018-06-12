using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace InnerLock
{
    public class LockMechanism2 : PartModule
    {
        // Runtime parameters
        [KSPField (isPersistant = true)]
        public uint lockFSMState = 0;

        [KSPField (isPersistant = true)]
        public uint pairLockPartId = 0;

        // Config parameters
        [KSPField]
        public bool canLockToOtherShip = false;

        [KSPField]
        public bool ecConstantDrain = false;

        [KSPField]
        public float ecConsumption = 1f;

        [KSPField]
        public string lockingTo = "lockLatch";

        [KSPField]
        public float lockStrength = 50f;

        [KSPField]
        public float maxOffset = 0.01f;

        [KSPField]
        public float maxRollDeviation = 0.01f;

        [KSPField]
        public string allowedRolls = "0";

        [KSPField (isPersistant = true)]
        public bool isSlave = false;

        [KSPField (isPersistant = true)]
        public bool isMaster = false;

        public float[] configuredRolls = { 0.0f };

        // Runtime attributes
        public ConfigurableJoint lockJoint;
        public AttachNode attachNode;
        public PartJoint partJoint;

        private FXGroup lockSound;
        private FXGroup unlockSound;

        public string unlockSoundPath = "InnerLock/Sounds/unlock";
        public string lockSoundPath = "InnerLock/Sounds/lock";

        private LockFSM lockFSM;
        private Part otherLockPart;
        private LockMechanism2 otherLock;

        private bool msgPosted = false;
        
        public LockMechanism2 ()
        {
        }

        public override void OnStart (StartState state)
        {
            base.OnStart (state);
            if (state != StartState.Editor) {
                GameEvents.onVesselGoOffRails.Add (offRails);
                GameEvents.onVesselGoOnRails.Add (onRails);
                GameEvents.onPartJointBreak.Add (partJointBreak);
                GameEvents.onJointBreak.Add (jointBreak);
                GameEvents.onPartDie.Add (partDie);
            }

            List<float> rolls = new List<float> ();
            foreach (string roll in allowedRolls.Split (',')) {
                rolls.Add (float.Parse (roll.Trim ()));
            }
            printDebug ("allowed rolls: " + allowedRolls);
            
            lockFSM = new LockFSM((LockFSM.State)lockFSMState);
            lockFSM.actionDelegates.Add("Engage", engageLock);
            lockFSM.actionDelegates.Add("Disengage", disengageLock);
            lockFSM.actionDelegates.Add("Lock", lockToLatch);
            lockFSM.actionDelegates.Add("Locked", finalizeLock);
            lockFSM.actionDelegates.Add("Slip", latchSlip);
        }

        public void OnDestroy ()
        {
            GameEvents.onVesselGoOffRails.Remove (offRails);
            GameEvents.onVesselGoOnRails.Remove (onRails);
            GameEvents.onPartJointBreak.Remove (partJointBreak);
            GameEvents.onJointBreak.Remove (jointBreak);
            GameEvents.onPartDie.Remove (partDie);
        }

        // Menu event handler
        [KSPEvent(guiName = "Disengage Lock", guiActive = true, guiActiveEditor = false, name = "disengageLock")]
        public void eventDisengage()
        {
            lockFSM.processEvent(LockFSM.Event.Disengage);
        }
        
        // Action group handler
        [KSPAction ("Disengage Lock", actionGroup = KSPActionGroup.None)]
        public void actionDisenage(KSPActionParam param)
        {
            lockFSM.processEvent(LockFSM.Event.Disengage);
        }
        
        public void disengageLock ()
        {
            setEmissiveColor ();
            setMenuEvents();
        }
        
        // Menu event handler
        [KSPEvent (guiName = "Engage Lock", guiActive = true, guiActiveEditor = false, name = "engageLock")]
        public void eventEngage ()
        {
            lockFSM.processEvent(LockFSM.Event.Engage);
        }

        // Action group handler
        [KSPAction("Engage Lock", actionGroup = KSPActionGroup.None)]
        public void actionEngage(KSPActionParam param)
        {
            lockFSM.processEvent(LockFSM.Event.Engage);
        }

        public void engageLock()
        {
            setEmissiveColor ();
            setMenuEvents();
        }

        // Action group handler for toggling lock on and off
        [KSPAction("Toggle Lock", actionGroup = KSPActionGroup.None)]
        public void actionToggle(KSPActionParam param)
        {
            // Send unlock event if in locked state, disengage otherwise
            // We just fire the event, FSM will sort out the proper reaction
            lockFSM.processEvent(lockFSM.state == LockFSM.State.Locked ? 
                LockFSM.Event.Unlock : LockFSM.Event.Disengage);
        }

        //  Railing and derailing stuff
        public void onRails (Vessel v)
        {
            // If we're going on rails when locking is in progress, reset to the ready
            if (lockFSM.state == LockFSM.State.Locking)
            {
                lockFSM.processEvent(LockFSM.Event.Disengage);
            }

            // If we're going on rails when unlocking is in progress, fast forward to idle 
            if (lockFSM.state == LockFSM.State.Unlocking)
            {
                lockFSM.processEvent(LockFSM.Event.Release);
            }
            // Just hide the menu buttons
            Events ["engageLock"].active = false;
            Events ["disengageLock"].active = false;
        }

        public void offRails (Vessel v)
        {
            setEmissiveColor ();
            setMenuEvents();
            
            if (lockFSM.state == LockFSM.State.Locked && lockJoint == null && pairLockPartId != 0) {
                // If locked and there's no joint, we're restoring from save and joint
                // must be re-created
                printDebug ("restoring joint; pair=" + pairLockPartId + "; isMaster=" + isMaster);
                otherLockPart = FlightGlobals.FindPartByID (pairLockPartId);
                if (otherLockPart != null) {
                    // In case of genderless locking, get other part locking module
                    if ((isMaster || isSlave) && otherLockPart.Modules.Contains ("LockMechanism2")) {
                        otherLock = (LockMechanism2)otherLockPart.Modules ["LockMechanism2"];
                    }
                    lockHasp (otherLockPart, true);
                }
            }

            msgPosted = false;
        }
        
        // Collision processing
        public void OnCollisionEnter (Collision c)
        {
            // Lock halves start touching each other. Send Contact event to FSM
            if (checkCollision(c))
            {
                lockFSM.processEvent(LockFSM.Event.Contact);
            }
        }	

        public void OnCollisionExit (Collision c)
        {
            // No more contact. Send Slip event to FSM
            lockFSM.processEvent(LockFSM.Event.Slip);
            msgPosted = false;
        }

        public void OnCollisionStay (Collision c)
        {
            // Lock halves continue touching each other. Activate locking mechanism if not already done
            if (checkCollision(c))
            {
                lockFSM.processEvent(LockFSM.Event.Contact);
            }
        }

        private bool checkCollision(Collision c)
        {
            foreach(ContactPoint cp in c.contacts) {

                otherLockPart = cp.otherCollider.attachedRigidbody.GetComponent<Part>();
        
                // Check if other part is suitable
                if (!checkOtherHalf(otherLockPart))
                continue;
        
                // Check if it is properly aligned 
                if (!checkRollAndDot(otherLockPart))
                continue;

                return true;
            }
            return false;
        }

        // Check if other lock half is suitable for locking
        private bool checkOtherHalf(Part otherPart) {

            if (otherPart == null) {
                return false;
            }

            if (!otherPart.name.Replace('.','_').Equals (lockingTo.Replace('.','_'))) {
                return false;
            }

            if (!canLockToOtherShip && otherPart.vessel != vessel) {
                printDebug("canLockToOtherShip = " + canLockToOtherShip + 
                           "; other vessel = " + otherPart.vessel.name);
                return false;
            }
            return true;
        }

        // Check if other half is properly aligned 
        private bool checkRollAndDot(Part otherPart) {

            float dotup = Vector3.Dot (otherPart.transform.up, transform.up);
            float dotfwd = Vector3.Dot (otherPart.transform.forward, transform.forward);
            float offset = Vector3.Distance (
                Vector3.ProjectOnPlane (transform.position, transform.up), 
                Vector3.ProjectOnPlane (otherPart.transform.position, transform.up));

            bool aligned = !(-dotup < maxRollDeviation || offset > maxOffset);

            foreach (float roll in allowedRolls) {
                if (Math.Abs (dotfwd - roll) < maxRollDeviation) {
                    aligned = true;
                    break;
                }
            }

            if (!aligned) {
                if (!msgPosted) {
                    printDebug ("dotup = " + dotup + "; dotfwd = " + dotfwd + "; offset = " + offset);
                    ScreenMessages.PostScreenMessage ("Latch not aligned - can't lock");
                    msgPosted = true;
                }
                return false;
            }
            return true;
        }

        public void lockToLatch()
        {
            setEmissiveColor();
            setMenuEvents();
            lockHasp(otherLockPart, false);
        }
        
        public void lockHasp (Part latch, bool isRelock)
        {
            printDebug ("lockHasp; part = " + latch.name + "; id = " + latch.flightID + "; relock = " + isRelock);
            // If we're not restoring the state after timewarp/load, perform
            // what it takes to lock the latch
            if (!isRelock) {
                float num = part.RequestResource ("ElectricCharge", ecConsumption);
                if (num < ecConsumption) {
                    ScreenMessages.PostScreenMessage ("Not enough electric charge to lock the hasp!");
                    return;
                }
                isSlave = false;
                // If we use genderless locking, tell the other part that we are leading
                if (latch.name == part.name && latch.Modules.Contains ("LockMechanism2")) {
                    // Both locks could be primed. In that case assing master status
                    // to the part with lesser flightID
                    otherLock = (LockMechanism2)latch.Modules ["LockMechanism2"];
                    if (part.flightID < latch.flightID) {
                        printDebug ("acquiring master status");
                        otherLock.setSlaveLock (part.flightID);
                        isMaster = true;
                    } else {
                        printDebug ("submitting to slave status");
                        isSlave = true;
                    }
                }
					
                if (!isSlave) {
                    if (lockSound == null)
                        lockSound = createAudio (part.gameObject, lockSoundPath);
                    lockSound.audio.Play ();
                }
            }
            StartCoroutine (finalizeLock (latch, isRelock));
        }
        
        // Signalled by master about locking. Set flags
		public void setSlaveLock(uint masterPartId) {
			isSlave = true;
			isMaster = false;
			pairLockPartId = masterPartId;
			Part otherLockPart = FlightGlobals.FindPartByID (masterPartId);
			otherLock = (LockMechanism2)otherLockPart.Modules ["LockMechanism2"];
			lockFSM.processEvent(LockFSM.Event.Lock);
		}

        public void latchSlip()
        {
            printDebug ("latch slipped");
            pairLockPartId = 0;
            ScreenMessages.PostScreenMessage ("Latch slipped! Can't lock");
        }

		// Locking takes some time to complete. Set up joint after sound has done playing
		public IEnumerator finalizeLock (Part latch, bool isRelock)
		{
			printDebug ("finalize lock; other part=" + latch.flightID);
			pairLockPartId = latch.flightID;
			if (!isRelock)
				yield return new WaitForSeconds (lockSound.audio.clip.length);

			
			if (lockFSM.state != LockFSM.State.Locking) {
				// Disengaged during the lock
				printDebug("state is not 'locking', aborting");
				pairLockPartId = 0;
				yield break;
			}

			if (!isSlave) {
				printDebug ("creating joint");
				lockJoint = part.gameObject.AddComponent<ConfigurableJoint> ();
				lockJoint.connectedBody = latch.rb;
				lockJoint.breakForce = lockStrength;
				lockJoint.breakTorque = lockStrength;
				lockJoint.xMotion = ConfigurableJointMotion.Locked;
				lockJoint.yMotion = ConfigurableJointMotion.Locked;
				lockJoint.zMotion = ConfigurableJointMotion.Locked;
				lockJoint.angularXMotion = ConfigurableJointMotion.Locked;
				lockJoint.angularYMotion = ConfigurableJointMotion.Locked;
				lockJoint.angularZMotion = ConfigurableJointMotion.Locked;
				lockJoint.linearLimit = new SoftJointLimit { bounciness = 0.9f, contactDistance = 0, limit = 0.01f };
				lockJoint.linearLimitSpring = new SoftJointLimitSpring { damper = 10000, spring = 0 };
				lockJoint.projectionMode = JointProjectionMode.PositionAndRotation;
				lockJoint.projectionDistance = 0;
				lockJoint.projectionAngle = 0;
				lockJoint.targetPosition = latch.transform.position;
				lockJoint.anchor = latch.transform.position;

				printDebug ("creating attachNode");

				Vector3 normDir = (part.transform.position - latch.transform.position).normalized;
				

				attachNode = new AttachNode {id = Guid.NewGuid().ToString(), attachedPart = latch};
				attachNode.breakingForce = lockStrength;
				attachNode.breakingTorque = lockStrength;
				attachNode.position = latch.partTransform.InverseTransformPoint(latch.partTransform.position);
				attachNode.orientation = latch.partTransform.InverseTransformDirection(normDir);
				attachNode.size = 1;
				attachNode.ResourceXFeed = false;
				attachNode.attachMethod = AttachNodeMethod.FIXED_JOINT;
				part.attachNodes.Add(attachNode);
				attachNode.owner = part;
				partJoint = PartJoint.Create(part, latch, attachNode, null, AttachModes.SRF_ATTACH);

				printDebug ("locked");
				if (!isRelock)
					ScreenMessages.PostScreenMessage ("Latch locked");

			}
				
			if (isMaster) {
				printDebug ("master; otherLock id = " + otherLock.part.flightID);
			}
            lockFSM.processEvent(LockFSM.Event.Lock);

		}

        public FixedJoint createJoint(Part target) {
            FixedJoint joint = part.gameObject.AddComponent<FixedJoint> ();
            joint.connectedBody = target.rb;
            joint.breakForce = lockStrength;
            joint.breakTorque = lockStrength;
            return joint;
        }

        // Master signalled lock completion
        public void finalizeLock() {

            setEmissiveColor();
            setMenuEvents();
        }

        // Utility methods
        private void setMenuEvents()
        {
            switch (lockFSM.state)
            {
                case LockFSM.State.Idle:
                    Events["engageLock"].active = true;
                    Events["disengageLock"].active = false;
                    Events["unlockLock"].active = false;
                    break;
                
                case LockFSM.State.Ready:
                case LockFSM.State.Locking:
                    Events["engageLock"].active = false;
                    Events["disengageLock"].active = true;
                    Events["unlockLock"].active = false;
                    break;
                
                case LockFSM.State.Locked:
                    Events["engageLock"].active = false;
                    Events["disengageLock"].active = false;
                    Events["unlockLock"].active = true;
                    break;
                
                case LockFSM.State.Unlocking:
                case LockFSM.State.Broken:
                    Events["engageLock"].active = false;
                    Events["disengageLock"].active = false;
                    Events["unlockLock"].active = false;
                    break;
                
            }
        }
        
        private void setEmissiveColor()
        {
            Color color;
            if (lockFSM.state == LockFSM.State.Ready)
            {
                color = Color.yellow;
            }
            else if(lockFSM.state == LockFSM.State.Locked)
            {
                color = Color.green;
            }
            else if (lockFSM.state == LockFSM.State.Locking || lockFSM.state == LockFSM.State.Unlocking)
            {
                color = Color.cyan;
            }
            else
            {
                color = Color.black;
            }
            
            Renderer renderer = gameObject.GetComponentInChildren<Renderer> ();
		
            if (renderer != null) {
                renderer.material.SetColor ("_EmissiveColor", color);
            }
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
                printDebug ("No clip found with path " + audioPath);
            }
            return fXGroup;
        }

        internal void printDebug(String message) {

            StackTrace trace = new StackTrace ();
            String caller = trace.GetFrame(1).GetMethod ().Name;
            int line = trace.GetFrame (1).GetFileLineNumber ();
            print ("LockMechanism: " + caller + ":" + line + ": (part id=" + part.flightID + "): " + message);
        }
    }
}