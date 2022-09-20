
namespace funnel_.core
{
    public class Dependency
    {
        public static Dependency Container {
            get{
                return _ownInstance;
            }
            private set{

            }
        }
        private static Dependency _ownInstance;
        private readonly Dictionary<Type, object> types = new Dictionary<Type, object>();
        private Dependency() {
        }

        static Dependency() {
            if(_ownInstance == null) {
                _ownInstance = new Dependency();
            }
        }

        public void Set<T>()
        {
            types.Add(typeof(T), null);
        }

        public void Set<T>(T instance)
        {
            types.Add(typeof(T), instance);
        }

        public T Get<T>() {
            if(types.ContainsKey(typeof(T)) && types[typeof(T)] != null) {
                return (T)types[typeof(T)];
            } else if(types.ContainsKey(typeof(T))) {
                var instance = Create(typeof(T));
                types[typeof(T)] = instance;
                return (T)instance;
            }

            return default(T);
        }

        private object Create(Type type)
        {
            var constructorInfos = type.GetConstructors()[0]; 
            var parameterInfos = constructorInfos.GetParameters();
            var parameters = parameterInfos.Select(parameter => Create(parameter.ParameterType)).ToArray();

            return constructorInfos.Invoke(parameters);
        }

    }
}

