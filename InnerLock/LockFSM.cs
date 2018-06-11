namespace InnerLock
{
    public class LockFSM
    {
        // Lock Finite State Machine   

        public enum State
        {
            Idle,
            Ready,
            Locking,
            Locked,
            Unlocking,
            Broken
        }

        public enum Transition
        {
            Engage,
            OnContact,
            OnSlip,
            SuccessfulLock,
            DisengageFromReady,
            DisengageFromLocking,
            DisengageFromLocked,
            SuccessfulUnlock,
            Breaking
        }

        public State state;

        public LockFSM()
        {
            state = State.Idle;
        }

        public LockFSM(State initialState)
        {
            state = initialState;
        }
    }
}