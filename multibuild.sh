#!/bin/bash

PROJECT="InnerLock"
KSP_BASE="${HOME}/ksp-versions"

TARGETS="1.3.1 1.5.1 1.6.1"

PROJECT_VERSION=$(cat GameData/${PROJECT}/$PROJECT.version|jq '.VERSION.MAJOR,.VERSION.MINOR,.VERSION.PATCH'|tr '\n' '.'|sed -e s'/\.$//')

MSBUILD="mono /usr/lib/mono/msbuild/15.0/bin/MSBuild.dll"

SOURCE_DIR=$(pwd)

for t in ${TARGETS}; do

  KSP_DIR=$(ls -d ${KSP_BASE}/ksp-${t}-{dev,vanilla} 2>/dev/null|head -1) 
  if [ "x${KSP_DIR}" != "x" ]; then
  
    echo "Building target ${t}..."
    
    TMPDIR=$(mktemp -d /tmp/kspbuild-XXXX)
    
    cp -r . ${TMPDIR}
    cd ${TMPDIR}
    export ASSEMBLY_PATH=$(echo ${KSP_DIR}|sed -e 's%\/%\\%g')
    cat ${PROJECT}/${PROJECT}.csproj|\
      perl -ne '$V=$ENV{"ASSEMBLY_PATH"}; s/<HintPath>(.*\\ksp-versions\\)(ksp-[^\\]+)(\\.*)/<HintPath>$V$3/; print $_' > tmp.csproj
    mv tmp.csproj ${PROJECT}/${PROJECT}.csproj

    KSP_BUILD_VERSION=$(echo $t|sed -e s'/\.//g')
    export SolutionDir=$(pwd)
    ${MSBUILD} /p:DefineConstants="KSP_${KSP_BUILD_VERSION}"
    PACKAGE="${PROJECT}-${PROJECT_VERSION}-ksp-$t.zip"
    zip -r ${PACKAGE} GameData
    mv ${PACKAGE} ${SOURCE_DIR}
    cd ${SOURCE_DIR}
    rm -rf ${TMPDIR}
  fi

done
