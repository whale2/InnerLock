using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UniLinq;
using UnityEngine;

namespace InnerLock
{
    public class LockMechanism : PartModule
    {
        // Runtime parameters
        [KSPField (isPersistant = true)]
        public uint lockFSMState;

        [KSPField (isPersistant = true)]
        public uint pairLockPartId;

        // Config parameters
        [KSPField]
        public bool canLockToOtherShip;

        [KSPField]
        public bool ecConstantDrain;

        [KSPField]
        public float ecConsumption = 1f;

        [KSPField]
        public string[] lockingTo = {"lockLatch", "IR_LockConnector"};
        
        [KSPField]
        public string lockTransform = "Body";


        [KSPField]
        public float lockStrength = 50f;

        [KSPField]
        public float maxOffset = 0.05f;

        [KSPField]
        public float maxRollDeviation = 0.01f;

        [KSPField]
        public string allowedRolls = "0";

        [KSPField (isPersistant = true)]
        public bool isSlave;

        [KSPField (isPersistant = true)]
        public bool isMaster;

        [KSPField(isPersistant = true)]
        public string startLockedStr;

        public bool startLocked;

        // Runtime attributes
        public AttachNode attachNode;
        public PartJoint lockJoint;
        public ConfigurableJoint cJoint;
        public SpringJoint sJoint;

        private FXGroup lockSound;
        private FXGroup unlockSound;

        public string unlockSoundPath = "InnerLock/Sounds/unlock";
        public string lockSoundPath = "InnerLock/Sounds/lock";

        private LockFSM lockFSM;
        private Part otherLockPart;
        private LockMechanism otherLock;

        private bool msgPosted;

        public override void OnStart (StartState state)
        {
            base.OnStart (state);
            printDebug($"startLocked = {startLockedStr}");
            if (startLockedStr == null)
            {
                startLocked = false;
            }
            else
            {
                startLocked = String.Equals(startLockedStr, "true", StringComparison.OrdinalIgnoreCase);
            }

            printDebug($"state {state}");

            
            if (state != StartState.Editor) {
                GameEvents.onVesselGoOffRails.Add (offRails);
                GameEvents.onVesselGoOnRails.Add (onRails);
                GameEvents.onPartJointBreak.Add (partJointBreak);
                GameEvents.onPartDie.Add (partDie);
                GameEvents.onFlightReady.Add(onFlightReady);
            }
            else
            {
                Events["setStartLocked"].active = !startLocked;
                Events["unsetStartLocked"].active = startLocked;
            }
            
            List<float> rolls = new List<float> ();
            foreach (string roll in allowedRolls.Split (',')) {
                rolls.Add (float.Parse (roll.Trim ()));
            }
            printDebug ("allowed rolls: " + allowedRolls);
            
            lockFSM = new LockFSM((LockFSM.State)lockFSMState);
            lockFSM.actionDelegates.Add("Engage", defaultPostEventAction);
            lockFSM.actionDelegates.Add("Disengage", defaultPostEventAction);
            lockFSM.actionDelegates.Add("Lock", lockToLatch);
            lockFSM.actionDelegates.Add("Locked", defaultPostEventAction);
            lockFSM.actionDelegates.Add("Slip", latchSlip);
            lockFSM.actionDelegates.Add("Unlock", unlockHasp);
            lockFSM.actionDelegates.Add("Release", defaultPostEventAction);
            lockFSM.actionDelegates.Add("Unlocked", defaultPostEventAction);
            lockFSM.actionDelegates.Add("Break", defaultPostEventAction);
        }

        public void onFlightReady()
        {
            if (vessel.situation== Vessel.Situations.PRELAUNCH)
            {
                processPreLaunch(vessel);
                printDebug("Done processing prelaunch");
            }
        }
        
        public void OnDestroy ()
        {
            GameEvents.onVesselGoOffRails.Remove (offRails);
            GameEvents.onVesselGoOnRails.Remove (onRails);
            GameEvents.onPartJointBreak.Remove (partJointBreak);
            GameEvents.onPartDie.Remove (partDie);
            GameEvents.onFlightReady.Remove(onFlightReady);
        }
        
        [KSPEvent(guiName = "Start Locked", guiActive = true, guiActiveEditor = true, name = "setStartLocked")]
        public void setStartLocked()
        {
            startLocked = true;
            Events["setStartLocked"].active = false;
            Events["unsetStartLocked"].active = true;
            startLockedStr = "true";
        }

        [KSPEvent(guiName = "Start Unlocked", guiActive = true, guiActiveEditor = true, name = "unsetStartLocked")]
        public void unsetStartLocked()
        {
            startLocked = false;
            Events["setStartLocked"].active = true;
            Events["unsetStartLocked"].active = false;
            startLockedStr = "false";
        }

        // Menu event handler
        [KSPEvent(guiName = "Disengage Lock", guiActive = true, guiActiveEditor = false, name = "eventDisengage")]
        public void eventDisengage()
        {
            lockFSM.processEvent(LockFSM.Event.Disengage);
        }
        
        // Action group handler
        [KSPAction ("Disengage Lock/Unlock", actionGroup = KSPActionGroup.None)]
        public void actionDisengage(KSPActionParam param)
        {
            // Send unlock event if in locked state, disengage otherwise
            if (lockFSM.state == LockFSM.State.Locked)
            {
                eventUnlock();
            }
            else
            {
                eventDisengage();
            }
        }
        
        // Menu event handler
        [KSPEvent (guiName = "Engage Lock", guiActive = true, guiActiveEditor = false, name = "eventEngage")]
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

        // Action group handler for toggling lock on and off
        [KSPAction("Toggle Lock", actionGroup = KSPActionGroup.None)]
        public void actionToggle(KSPActionParam param)
        {
            switch (lockFSM.state)
            {
                case LockFSM.State.Locked:
                    eventUnlock();
                    break;
                case LockFSM.State.Ready:
                case LockFSM.State.Locking:
                    eventDisengage();
                    break;
                case LockFSM.State.Idle:
                    eventEngage();
                    break;
            }
        }
        
        // Menu event handler
        [KSPEvent(guiName = "Unlock", guiActive = true, guiActiveEditor = false, name = "eventUnlock")]
        public void eventUnlock()
        {
            lockFSM.processEvent(LockFSM.Event.Unlock);
            processCounterpart(LockFSM.Event.Unlock);
        }

        public void processPreLaunch(Vessel v)
        {
            printDebug($"new vessel: {v}");
            if (v != part.vessel || !startLocked)
            {
                return;
            }

            // Raycast upright and if there's a lock, create joint
            Part onLaunchCounterPart = findCounterpartOnLaunch();
            if (onLaunchCounterPart == null)
            {
                lockFSM.state = LockFSM.State.Ready;
                return;
            }
            
            // check if other part has id greater than ours and LaunchLocked set
            // if so, we give up setting a lock and submit to slave status
            printDebug($"Our id: {part.flightID}, theirs id: {onLaunchCounterPart.flightID}");
            otherLock = (LockMechanism) onLaunchCounterPart.Modules["LockMechanism"];
            if (onLaunchCounterPart.flightID > part.flightID)
            {
                if (otherLock.startLockedStr.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            otherLock.setSlaveLock(part.flightID);
            pairLockPartId = onLaunchCounterPart.flightID;
            lockFSM.state = LockFSM.State.Locked;
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
            Events ["eventEngage"].active = false;
            Events ["eventDisengage"].active = false;
            Events ["eventUnlock"].active = false;
        }

        public void offRails (Vessel v)
        {
            if (lockFSM.state == LockFSM.State.Locked && lockJoint == null && pairLockPartId != 0) {
                // If locked and there's no joint, we're restoring from save and joint
                // must be re-created
                printDebug ("restoring joint; pair=" + pairLockPartId + "; isMaster=" + isMaster);
                otherLockPart = FlightGlobals.FindPartByID (pairLockPartId);
                if (otherLockPart != null) {
                    // In case of genderless locking, get other part locking module
                    if ((isMaster || isSlave) && otherLockPart.Modules.Contains ("LockMechanism")) {
                        otherLock = (LockMechanism)otherLockPart.Modules ["LockMechanism"];
                    }
                    lockHasp (otherLockPart);
                }
            }

            defaultPostEventAction();

            msgPosted = false;
        }

        public Part findCounterpartOnLaunch()
        {
            // Raycast upwards, return true if there's a lockable part within proper distance and aligned
            int rayCastMask = 0xFFFF;
            RaycastHit[] hits = new RaycastHit[5]; // Say, 5 colliders ought to be enough for anyone
            
            Transform tf = part.FindModelTransform(lockTransform);
            
//            LineRenderer lr = new GameObject ().AddComponent<LineRenderer> ();
//            lr.material = new Material (Shader.Find ("KSP/Emissive/Diffuse"));
//            lr.useWorldSpace = true;
//            lr.material.SetColor ("_EmissiveColor", Color.green);
//            /*Lr0.startWidth = 0.15f;
//            Lr0.endWidth = 0.15f;
//            Lr0.positionCount = 4;*/
//            lr.SetVertexCount(2);
//            lr.SetWidth(0.05f,0.05f);
//            lr.enabled = true;
//            lr.SetPosition(0, tf.TransformPoint(Vector3.up * upperBound));
//            lr.SetPosition(1, tf.TransformPoint(Vector3.up));

            float upperBound = findPartUpperBound(tf);
            printDebug($"upper bound = {upperBound}");
            
            int nHits = Physics.RaycastNonAlloc(tf.position,tf.up, hits, upperBound * 1.5f, rayCastMask);
            printDebug($"Got {nHits} hits");
            for(int n = 0; n < nHits; n ++)
            {
                printDebug($"Got raycast hit {hits[n]}, transform: {hits[n].transform}, GO: {hits[n].transform.gameObject}" +
                           $"; distance: {hits[n].distance}; collider: {hits[n].collider}");
                Part hitPart = hits[n].transform.gameObject.GetComponentInParent<Part>();
                if (hitPart == null)
                {
                    printDebug("No part");
                    continue;
                }
                printDebug($"Hit part {hitPart}; id={hitPart.flightID}");
                if (hitPart.flightID == part.flightID)
                {
                    printDebug("Got hit to ourselves");
                    continue;
                }
                // Check if it is a lockable part at all
                if (!lockingTo.Any(hitPart.partInfo.name.Replace(".","_").Contains))
                {
                    printDebug("Part could not be locked to");
                    continue;
                }
                printDebug("Part could be locked to");
                // Ok, we can lock to it. Check alignment
                if (!checkRollAndDot(hitPart, upperBound))
                {
                    printDebug("Part not aligned");
                    return null;
                }
    
                printDebug($"Parts aligned, returning {hitPart}");
                return hitPart;
                }
            return null;
        }
        
        // Collision processing
        public void OnCollisionEnter (Collision c)
        {
            // Lock halves start touching each other. Send Contact event to FSM
            // But stop checks if collision fired when the lock is in the state from which it can't transit anywhere else 
            if (lockFSM.canTransit(LockFSM.Event.Contact) && checkCollision(c))
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
            // But stop checks if collision fired when the lock is in the state from which it can't transit anywhere else 
            if (lockFSM.canTransit(LockFSM.Event.Contact) && checkCollision(c))
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
                if (!checkRollAndDot(otherLockPart, maxOffset))
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

            if(!lockingTo.Any(otherPart.partInfo.name.Replace(".","_").Contains))
            //if (lockingTo.Any(s => otherPart.name.Replace('.','_').Equals(s.Replace('.','_')))) 
            {
            //if (!otherPart.name.Replace('.','_').Equals (lockingTo.Replace('.','_'))) {
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
        private bool checkRollAndDot(Part otherPart, float maxDistance) {

            float dotup = Vector3.Dot (otherPart.transform.up, transform.up);
            float dotfwd = Vector3.Dot (otherPart.transform.forward, transform.forward);
            float offset = Vector3.Distance (
                Vector3.ProjectOnPlane (transform.position, transform.up), 
                Vector3.ProjectOnPlane (otherPart.transform.position, transform.up));

            bool aligned = !(-dotup < maxRollDeviation || offset > maxDistance);

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
            defaultPostEventAction();
            printDebug($"other lock part = {otherLockPart.flightID}");
            lockHasp(otherLockPart);
        }
        
        public void lockHasp (Part latch)
        {
            printDebug ($"part={latch.name}; id={latch.flightID}; fsm state={lockFSM.state}");
            // If we're not restoring the state after timewarp/load, perform
            // what it takes to lock the latch
            if (lockFSM.state == LockFSM.State.Locking)
            {
    #if (KSP_151 || KSP_161)
                float num = (float)part.RequestResource("ElectricCharge", (double) (new decimal(ecConsumption)));
    #else
                float num = part.RequestResource ("ElectricCharge", ecConsumption);              
    #endif
                if (num < ecConsumption) {
                    ScreenMessages.PostScreenMessage ("Not enough electric charge to lock the hasp!");
                    return;
                }
                isSlave = false;
                // If we use genderless locking, tell the other part that we are leading
                if (latch.name == part.name && latch.Modules.Contains ("LockMechanism")) {
                    // Both locks could be primed. In that case assign master status
                    // to the part with lesser flightID
                    otherLock = (LockMechanism)latch.Modules ["LockMechanism"];
                    printDebug($"our fsm state = {lockFSM.state}, other fsm state = {otherLock.lockFSM.state}");
                    if (otherLock.lockFSM.state != LockFSM.State.Locking || part.flightID < latch.flightID) {
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
            StartCoroutine (finalizeLock (latch));
        }
        
        // Signalled by master about locking. Set flags
		public void setSlaveLock(uint masterPartId) {
		    printDebug($"setting slave lock by request from {masterPartId}");
			isSlave = true;
			isMaster = false;
			pairLockPartId = masterPartId;
			otherLockPart = FlightGlobals.FindPartByID (masterPartId);
			otherLock = (LockMechanism)otherLockPart.Modules ["LockMechanism"];
		    lockFSM.state = LockFSM.State.Locking;
		    defaultPostEventAction();
		}

        public void latchSlip()
        {
            printDebug ("latch slipped");
            pairLockPartId = 0;
            ScreenMessages.PostScreenMessage ("Latch slipped! Can't lock");
            lockFSM.state = LockFSM.State.Ready;
            defaultPostEventAction();
        }

        public void createAttachNode(Part latch)
        {
            printDebug($"creating attachNode from part {part.flightID} to part {latch.flightID}");
            
            part.attachJoint.Joint.breakForce = Mathf.Infinity;
            part.attachJoint.Joint.breakTorque = Mathf.Infinity;

            latch.attachJoint.Joint.breakForce = Mathf.Infinity;
            latch.attachJoint.Joint.breakTorque = Mathf.Infinity;
            
            cJoint = part.gameObject.AddComponent<ConfigurableJoint>();
            cJoint.connectedBody = latch.GetComponent<Rigidbody>();
            cJoint.breakForce = cJoint.breakTorque = Mathf.Infinity;
            cJoint.xMotion = ConfigurableJointMotion.Locked;
            cJoint.yMotion = ConfigurableJointMotion.Locked;
            cJoint.zMotion = ConfigurableJointMotion.Locked;
            cJoint.angularXMotion = ConfigurableJointMotion.Locked;
            cJoint.angularYMotion = ConfigurableJointMotion.Locked;
            cJoint.angularZMotion = ConfigurableJointMotion.Locked;
            cJoint.projectionAngle = 0f;
            cJoint.projectionDistance = 0f;
            cJoint.targetPosition = latch.transform.position;
            cJoint.anchor = part.transform.position;

            // FIXME: Spring or Fixed?
            sJoint = part.parent.gameObject.AddComponent<SpringJoint>();
            sJoint.damper = lockStrength / 2f;
            sJoint.maxDistance = 0;
            sJoint.minDistance = 0;
            sJoint.spring = lockStrength * 10f;
            sJoint.tolerance = 0.005f;
            sJoint.connectedBody = latch.parent.GetComponent<Rigidbody>();
                
                
            printDebug($"Created configurable joint with id={cJoint.GetInstanceID()}; joint={cJoint}");
        
            Vector3 normDir = (part.transform.position - latch.transform.position).normalized;
            attachNode = new AttachNode();
            attachNode.id = Guid.NewGuid().ToString();
            attachNode.attachedPart = latch;
            attachNode.owner = part;
            attachNode.breakingForce = lockStrength;
            attachNode.breakingTorque = lockStrength;
            attachNode.position = latch.partTransform.InverseTransformPoint(latch.partTransform.position);
            attachNode.orientation = latch.partTransform.InverseTransformDirection(normDir);
            attachNode.size = 1;
            attachNode.ResourceXFeed = false;
            attachNode.attachMethod = AttachNodeMethod.FIXED_JOINT;
            part.attachNodes.Add(attachNode);
            attachNode.owner = part;
            lockJoint = PartJoint.Create(part, latch, attachNode, null, AttachModes.SRF_ATTACH);
            printDebug($"Created lockJoint with id={lockJoint.GetInstanceID()}; joint={lockJoint}");
        }

		// Locking takes some time to complete. Set up joint after sound has done playing
		public IEnumerator finalizeLock (Part latch)
		{
			printDebug ($"finalize lock; other part={latch.flightID}, fsm state={lockFSM.state}, isSlave={isSlave}");
			pairLockPartId = latch.flightID;
			if (lockFSM.state == LockFSM.State.Locking) {
			    printDebug ("finalize lock; sleeping");
				WaitForSeconds wfs = new WaitForSeconds (lockSound.audio.clip.length);
			    printDebug ($"finalize lock; yielding {wfs}");
			    yield return wfs;
			}
			else if (lockFSM.state != LockFSM.State.Locked) {
				// Disengaged during the lock
				printDebug("state is not 'locking', aborting");
				pairLockPartId = 0;
				yield break;
			}

		    // At this point the latch could have slipped, because we waited for some time.
		    // Check if FSM state is still "locking"
		    if (lockFSM.state != LockFSM.State.Locking && lockFSM.state != LockFSM.State.Locked)
		    {
		        printDebug("Latch slipped while locking. Aborting lock.");
		        yield break;
		    }
		    
			if (!isSlave) {
				
			    createAttachNode(latch);
				printDebug ("locked");
				if (lockFSM.state == LockFSM.State.Locking)
					ScreenMessages.PostScreenMessage ("Latch locked");
			}
				
			if (isMaster) {
				printDebug ("master; otherLock id = " + otherLock.part.flightID);
			    // Sort of hack - other lock can be in almost any state except Broken 
			    otherLock.lockFSM.state = LockFSM.State.Locked;
			    otherLock.defaultPostEventAction();
			}
		    
		    printDebug($"part breaking force: {part.breakingForce}, own partJoint breaking force: {part.attachJoint.Joint.breakForce}");
		    foreach (var joint in part.attachJoint.joints)
		    {
		        printDebug($"other joints: {joint}, rb:{joint.connectedBody}, anchor: {joint.connectedAnchor}, breaking force: {joint.breakForce}");
		    }
		    
            lockFSM.processEvent(LockFSM.Event.Lock);
		}

        // Unlocking takes time as well
        public void unlockHasp ()
        {
            setEmissiveColor();
            setMenuEvents();
            if (unlockSound == null)
                unlockSound = createAudio (part.gameObject, unlockSoundPath);
			
            if (!isSlave) {
                // Not playing two sounds at once
                unlockSound.audio.Play ();
            }
            StartCoroutine (finalizeUnlock (false));
        }
			
        private IEnumerator finalizeUnlock (bool broken)
        {
            printDebug ("finalize unlock; master: " + isMaster + "; slave: " + isSlave + "; broken: " + broken);
            if (!broken) {
                yield return new WaitForSeconds (unlockSound.audio.clip.length);
            }

            if ((isSlave || isMaster) && otherLock.lockFSM.state != LockFSM.State.Unlocking) {
                StartCoroutine (otherLock.finalizeUnlock (true));
            }

            if (lockFSM.state == LockFSM.State.Unlocking)
            {
                // Process Unlocked event before destroying joint
                lockFSM.processEvent(LockFSM.Event.Release);
            }
            
            if (lockJoint != null) {
                printDebug ("destroying joint");
                lockJoint.DestroyJoint();
                part.attachNodes.Remove(attachNode);
                DestroyImmediate(cJoint);
                DestroyImmediate(sJoint);
                
                if (attachNode != null) {
                    //part.attachNodes.Remove (attachNode);
                    attachNode.owner = null;
                }
                //DestroyImmediate (lockJoint);
                printDebug(String.Format("Done destroying: attachNode={0}, partJoint={1}", 
                    attachNode, lockJoint));
            }

            //lockJoint = null;
            attachNode = null;
            cJoint = null;
            sJoint = null;
            lockJoint = null;
            isMaster = false;
            isSlave = false;
            pairLockPartId = 0;
        }

        public void partJointBreak(PartJoint joint, float breakForce) {
            // the lock got broken off of the part it was connected to.
            // Lock part should have two joints - one created during locking and one the lock is attached to some other
            // part. Let's deactivate lock in both cases
            if (Math.Abs(breakForce) < 0.01)
            {
                return;
            }
            printDebug($"broken PART joint; parent={joint.Parent.flightID}; owner={joint.Host.flightID}; our part id={part.flightID}; joint id={joint.GetInstanceID()}; joint={joint}; force={breakForce}");
            
            lockJoint.DestroyJoint();
            DestroyImmediate(cJoint);
            DestroyImmediate(sJoint);
            cJoint = null;
            sJoint = null;
            part.attachNodes.Remove(attachNode);
            attachNode.owner = null;
            // Seems like we just got separated from the vessel.
            // Shut down actions taking into account that we might be still
            // connected to other lock part
            printDebug ($"broken joint: {joint}, lock joint: {lockJoint}; part joint broken");
            lockFSM.processEvent(LockFSM.Event.Break);
        }

        public void partDie(Part p) {
            
            if (p != part) {
                return;
            }
            
            if (lockFSM.state == LockFSM.State.Locked) {
                StartCoroutine (finalizeUnlock (true));
            }
            lockFSM.processEvent(LockFSM.Event.Break);
        }
        
        private IEnumerator waitAndCheckJoint() {

            yield return new WaitForFixedUpdate();
            if (lockJoint == null) {
                printDebug ("lock joint broken");
                StartCoroutine (finalizeUnlock(true));
            }
        }
        
        // Utility methods
        public void defaultPostEventAction()
        {
            printDebug(String.Format("fsm state = {0}", lockFSM.state));
            lockFSMState = (uint) lockFSM.state;
            setEmissiveColor();
            setMenuEvents();
        }
        
        private void setMenuEvents()
        {
            switch (lockFSM.state)
            {
                case LockFSM.State.Idle:
                    Events["eventEngage"].active = true;
                    Events["eventDisengage"].active = false;
                    Events["eventUnlock"].active = false;
                    break;
                
                case LockFSM.State.Ready:
                case LockFSM.State.Locking:
                    Events["eventEngage"].active = false;
                    Events["eventDisengage"].active = true;
                    Events["eventUnlock"].active = false;
                    break;
                
                case LockFSM.State.Locked:
                    Events["eventEngage"].active = false;
                    Events["eventDisengage"].active = false;
                    Events["eventUnlock"].active = true;
                    break;
                
                case LockFSM.State.Unlocking:
                case LockFSM.State.Broken:
                    Events["eventEngage"].active = false;
                    Events["eventDisengage"].active = false;
                    Events["eventUnlock"].active = false;
                    break;
            }

            Events["setStartLocked"].active = HighLogic.LoadedSceneIsEditor;
            Events["unsetStartLocked"].active = HighLogic.LoadedSceneIsEditor;
        }
        
        private void processCounterpart(LockFSM.Event fsmEvent)
        {
            if (otherLock && (isMaster || isSlave))
            {
                otherLock.lockFSM.processEvent(fsmEvent);
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

        internal float findPartUpperBound(Transform tr)
        {
            List<Mesh> meshes = new List<Mesh>();
            tr.GetComponentsInChildren<MeshFilter>().ToList().ForEach(mf => meshes.Add(mf.mesh));
            //tr.GetComponentsInChildren<MeshCollider>().ToList().ForEach(mc => meshes.Add(mc.sharedMesh));

            float bound = 0.0f;
            foreach (var mesh in meshes)
            {
                foreach (Vector3 vert in mesh.vertices)
                {
                    if (vert.y > bound)
                    {
                        bound = vert.y;
                    }
                }
            }

            return bound;
        }
        
        internal void printDebug(String message) {

            StackTrace trace = new StackTrace ();
            String caller = trace.GetFrame(1).GetMethod ().Name;
            int line = trace.GetFrame (1).GetFileLineNumber ();
            print ("LockMechanism: " + caller + ":" + line + ": (part id=" + part.flightID + "): " + message);
        }
    }
}