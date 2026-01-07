set serviceName=FjWorkerService

sc stop   %serviceName% 
sc delete %serviceName% 

pause